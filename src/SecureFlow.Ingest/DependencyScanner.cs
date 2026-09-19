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

    /// <summary>Hard cap so a pathological monorepo manifest can't blow up one batch query.</summary>
    public int MaxPackages { get; init; } = 300;

    public DependencyScanner(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.osv.dev/");
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SecureFlow", "1.0"));
    }

    private sealed record Dep(string Ecosystem, string Name, string Version, string ManifestPath);

    public async Task<List<Finding>> ScanAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct)
    {
        var deps = ExtractDependencies(digest);
        if (deps.Count == 0)
        {
            progress?.Report("No recognizable dependency manifest (package.json, *.csproj, requirements.txt, go.mod) in the digest.");
            return new List<Finding>();
        }
        if (deps.Count > MaxPackages)
        {
            progress?.Report($"{deps.Count} dependencies found; checking the first {MaxPackages}.");
            deps = deps.Take(MaxPackages).ToList();
        }

        progress?.Report($"Checking {deps.Count} dependencies against OSV.dev...");
        var hits = await QueryBatchAsync(deps, ct);
        if (hits.Count == 0) { progress?.Report("No known vulnerabilities found."); return new List<Finding>(); }

        progress?.Report($"{hits.Count} advisory match(es); fetching details...");
        var findings = new List<Finding>();
        foreach (var (dep, vulnId) in hits)
        {
            var detail = await FetchVulnAsync(vulnId, ct);
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

    // ---------- manifest parsing (text already in the digest, nothing re-fetched) ----------

    private static List<Dep> ExtractDependencies(RepoDigest digest)
    {
        var deps = new List<Dep>();
        foreach (var file in digest.Files)
        {
            var name = System.IO.Path.GetFileName(file.Path);
            try
            {
                if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                    deps.AddRange(ParsePackageJson(file));
                else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    deps.AddRange(ParseCsproj(file));
                else if (name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase))
                    deps.AddRange(ParseRequirementsTxt(file));
                else if (name.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
                    deps.AddRange(ParseGoMod(file));
            }
            catch
            {
                // A malformed manifest shouldn't fail the whole scan; just skip it.
            }
        }
        // Same package can appear in multiple manifests (workspaces, multi-project repos); keep one.
        return deps.GroupBy(d => (d.Ecosystem, d.Name, d.Version)).Select(g => g.First()).ToList();
    }

    private static List<Dep> ParsePackageJson(RepoFile file)
    {
        var result = new List<Dep>();
        using var doc = JsonDocument.Parse(file.Content);
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in deps.EnumerateObject())
            {
                var version = CleanNpmVersion(prop.Value.GetString() ?? "");
                if (version is not null) result.Add(new Dep("npm", prop.Name, version, file.Path));
            }
        }
        return result;
    }

    private static string? CleanNpmVersion(string raw)
    {
        raw = raw.Trim();
        // Skip ranges/tags/git/workspace refs we can't resolve to one concrete version.
        if (raw.Length == 0 || raw is "*" or "latest" || raw.Contains(' ') || raw.Contains("||")
            || raw.StartsWith("git") || raw.StartsWith("file:") || raw.StartsWith("workspace:") || raw.StartsWith("link:"))
            return null;
        raw = raw.TrimStart('^', '~', '>', '<', '=').Trim();
        return Regex.IsMatch(raw, @"^\d+(\.\d+){1,3}") ? raw : null;
    }

    private static List<Dep> ParseCsproj(RepoFile file)
    {
        var result = new List<Dep>();
        foreach (Match m in Regex.Matches(file.Content,
            @"<PackageReference\s+[^>]*Include\s*=\s*""([^""]+)""[^>]*Version\s*=\s*""([^""]+)""|<PackageReference\s+[^>]*Version\s*=\s*""([^""]+)""[^>]*Include\s*=\s*""([^""]+)"""))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
            var version = m.Groups[1].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (Regex.IsMatch(version, @"^\d+(\.\d+){1,3}")) result.Add(new Dep("NuGet", name, version, file.Path));
        }
        return result;
    }

    private static List<Dep> ParseRequirementsTxt(RepoFile file)
    {
        var result = new List<Dep>();
        foreach (var rawLine in file.Content.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var m = Regex.Match(line, @"^([A-Za-z0-9_.-]+)\s*==\s*([0-9][0-9A-Za-z.+-]*)$");
            if (m.Success) result.Add(new Dep("PyPI", m.Groups[1].Value, m.Groups[2].Value, file.Path));
        }
        return result;
    }

    private static List<Dep> ParseGoMod(RepoFile file)
    {
        var result = new List<Dep>();
        foreach (Match m in Regex.Matches(file.Content, @"^\s*([a-zA-Z0-9._/-]+)\s+(v[0-9][0-9A-Za-z.+-]*)\s*(?://.*)?$", RegexOptions.Multiline))
            result.Add(new Dep("Go", m.Groups[1].Value, m.Groups[2].Value, file.Path));
        return result;
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

    private static Finding ToFinding(Dep dep, VulnDetail v)
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
                + (v.Severity is null ? " Severity was not published by the advisory; shown as Medium." : ""),
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

    private static Severity MapSeverity(string? osv) => osv?.ToUpperInvariant() switch
    {
        "CRITICAL" => Core.Model.Severity.Critical,
        "HIGH" => Core.Model.Severity.High,
        "MODERATE" => Core.Model.Severity.Medium,
        "LOW" => Core.Model.Severity.Low,
        _ => Core.Model.Severity.Medium,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Sha256Short(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }
}
