using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SecureFlow.Ai;
using SecureFlow.Core.Model;

namespace SecureFlow.Ingest;

/// <summary>
/// Real CVE/GHSA scanning against the repo's own dependency manifests — never against installed
/// packages. It only ever reads text already captured in the RepoDigest (package.json, *.csproj,
/// requirements.txt, go.mod); it never runs `npm install`, `dotnet restore` or any repo code, so a
/// malicious repo can't get code execution out of this the way an install-then-audit approach could.
/// Queries the OSV.dev API (osv.dev), Google's free, keyless, no-signup vulnerability database that
/// aggregates GitHub Advisories, npm/PyPI/NuGet/Go advisories and NVD — real published CVEs, not
/// AI-guessed ones.
/// </summary>
public sealed class DependencyScanner
{
    private readonly HttpClient _http;
    private readonly DependencyScanOptions _options;

    public DependencyScanner(HttpClient http, DependencyScanOptions options)
    {
        _http = http;
        _options = options;
        _http.BaseAddress = new Uri(options.OsvBaseUrl.EndsWith('/') ? options.OsvBaseUrl : options.OsvBaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SecureFlow", "1.0"));
    }

    public bool Enabled => _options.Enabled;

    private sealed record Dep(string Ecosystem, string Name, string Version, string ManifestPath);

    public async Task<List<Finding>> ScanAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct)
    {
        var deps = ExtractDependencies(digest);
        if (_options.Ecosystems.Length > 0)
            deps = deps.Where(d => _options.Ecosystems.Contains(d.Ecosystem, StringComparer.OrdinalIgnoreCase)).ToList();
        if (deps.Count == 0)
        {
            progress?.Report("No pinned dependencies found in manifests or lockfiles (pom.xml, build.gradle, package.json/lockfiles, *.csproj, requirements.txt, poetry.lock, go.mod...).");
            return new List<Finding>();
        }
        if (deps.Count > _options.MaxPackages)
        {
            progress?.Report($"{deps.Count} dependencies found; checking the first {_options.MaxPackages} (DependencyScan:MaxPackages).");
            deps = deps.Take(_options.MaxPackages).ToList();
        }

        progress?.Report($"Checking {deps.Count} dependencies ({string.Join(", ", deps.GroupBy(d => d.Ecosystem).Select(g => $"{g.Key} {g.Count()}"))}) against OSV...");
        var hits = new List<(Dep, string)>();
        foreach (var batch in deps.Chunk(Math.Clamp(_options.BatchSize, 1, 1000)))
            hits.AddRange(await QueryBatchAsync(batch.ToList(), ct));
        if (hits.Count == 0) { progress?.Report("No known vulnerabilities found."); return new List<Finding>(); }

        progress?.Report($"{hits.Count} advisory match(es); fetching details...");
        var findings = new List<Finding>();
        var details = new Dictionary<string, VulnDetail?>();
        foreach (var (dep, vulnId) in hits)
        {
            if (!details.TryGetValue(vulnId, out var detail)) details[vulnId] = detail = await FetchVulnAsync(vulnId, ct);
            if (detail is not null) findings.Add(ToFinding(dep, detail));
        }

        // A monorepo can pin the same package at different versions across workspaces (Immich's
        // server/, web/, e2e/ package.json each vendor their own), so the same advisory can be hit
        // more than once and hash to the same Finding id. Merge those into one finding with every
        // manifest as evidence, rather than dropping one or crashing on the duplicate id downstream.
        return findings.GroupBy(f => f.Id).Select(g =>
        {
            var first = g.First();
            first.Evidence = g.SelectMany(f => f.Evidence).DistinctBy(e => (e.Path, e.Snippet)).ToList();
            return first;
        }).ToList();
    }

    // ---------- dependencies (resolved when the digest was built; re-parsed for older digests) ----------

    private static List<Dep> ExtractDependencies(RepoDigest digest)
    {
        // Digests built by this version carry a resolved list (including lockfiles). Older stored digests
        // don't, so fall back to parsing whatever manifests made it into the file content.
        var refs = digest.Dependencies.Count > 0
            ? digest.Dependencies
            : ManifestParser.Distinct(digest.Files.Where(f => ManifestParser.IsManifest(f.Path)).SelectMany(f => ManifestParser.Parse(f.Path, f.Content)));
        return refs.Select(d => new Dep(d.Ecosystem, d.Name, d.Version, d.ManifestPath)).ToList();
    }

    // ---------- OSV.dev ----------

    private async Task<List<(Dep Dep, string VulnId)>> QueryBatchAsync(List<Dep> deps, CancellationToken ct)
    {
        var payload = new
        {
            queries = deps.Select(d => new { package = new { name = d.Name, ecosystem = d.Ecosystem }, version = d.Version }),
        };
        using var res = await _http.PostAsJsonAsync("v1/querybatch", payload, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"OSV.dev returned {(int)res.StatusCode}.");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var hits = new List<(Dep, string)>();
        var results = doc.RootElement.GetProperty("results");
        for (var i = 0; i < results.GetArrayLength() && i < deps.Count; i++)
        {
            if (!results[i].TryGetProperty("vulns", out var vulns) || vulns.ValueKind != JsonValueKind.Array) continue;
            foreach (var v in vulns.EnumerateArray())
                hits.Add((deps[i], v.GetProperty("id").GetString()!));
        }
        return hits;
    }

    public sealed record VulnDetail(string Id, string Summary, string Details, List<string> Aliases, string? Severity, string? FixedVersion);

    private async Task<VulnDetail?> FetchVulnAsync(string id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"v1/vulns/{id}", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
        var details = root.TryGetProperty("details", out var d) ? d.GetString() ?? "" : "";
        var aliases = root.TryGetProperty("aliases", out var a)
            ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
        var severity = root.TryGetProperty("database_specific", out var db) && db.TryGetProperty("severity", out var sv)
            ? sv.GetString() : null;

        string? fixedVersion = null;
        if (root.TryGetProperty("affected", out var affected))
            foreach (var aff in affected.EnumerateArray())
                if (aff.TryGetProperty("ranges", out var ranges))
                    foreach (var range in ranges.EnumerateArray())
                        if (range.TryGetProperty("events", out var events))
                            foreach (var ev in events.EnumerateArray())
                                if (ev.TryGetProperty("fixed", out var fx)) fixedVersion = fx.GetString();

        return new VulnDetail(id, summary, details, aliases, severity, fixedVersion);
    }

    // ---------- mapping to a Finding ----------

    private Finding ToFinding(Dep dep, VulnDetail v)
    {
        var cve = v.Aliases.FirstOrDefault(a => a.StartsWith("CVE-"));
        var ruleId = cve ?? v.Id;
        var severity = MapSeverity(v.Severity);
        return new Finding
        {
            Id = "V" + Sha256Short($"{dep.Ecosystem}|{dep.Name}|{v.Id}"),
            RuleId = ruleId,
            Category = FindingCategory.Vulnerability,
            Tag = dep.Ecosystem,
            Severity = severity,
            Confidence = Confidence.High,
            Source = FindingSource.Rule,
            Status = FindingStatus.Open,
            Title = $"{dep.Name}@{dep.Version}: {(string.IsNullOrWhiteSpace(v.Summary) ? ruleId : v.Summary)}",
            Description = string.IsNullOrWhiteSpace(v.Details) ? v.Summary : Truncate(v.Details, 900),
            Rationale = $"Published advisory {v.Id}{(cve is not null && cve != v.Id ? $" ({cve})" : "")} affects {dep.Name} {dep.Version}, referenced in {dep.ManifestPath}."
                + (v.Severity is null ? $" Severity was not published by the advisory; shown as {Unrated}." : ""),
            Mitigation = v.FixedVersion is not null
                ? $"Upgrade {dep.Name} to {v.FixedVersion} or later."
                : $"Upgrade {dep.Name} past the vulnerable range; check {v.Id} for the fixed version.",
            ComponentIds = new List<string>(),
            FlowIds = new List<string>(),
            Evidence = new List<Evidence>
            {
                new() { Source = "repo", Path = dep.ManifestPath, Snippet = $"{dep.Name}@{dep.Version} ({v.Id})" },
            },
        };
    }

    private Severity Unrated => Enum.TryParse<Severity>(_options.UnratedSeverity, true, out var s) ? s : Core.Model.Severity.Medium;

    private Severity MapSeverity(string? osv) => osv?.ToUpperInvariant() switch
    {
        "CRITICAL" => Core.Model.Severity.Critical,
        "HIGH" => Core.Model.Severity.High,
        "MODERATE" => Core.Model.Severity.Medium,
        "LOW" => Core.Model.Severity.Low,
        _ => Unrated,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Sha256Short(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }
}
