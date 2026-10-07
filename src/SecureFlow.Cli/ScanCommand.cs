using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecureFlow.Ai;
using SecureFlow.Core.Model;
using SecureFlow.Ingest;

namespace SecureFlow.Cli;

/// <summary>
/// `secureflow scan`: build the digest on this machine (no code leaves it except the selected,
/// redacted files and the resolved dependency list), upload it to a SecureFlow server, wait for the
/// analysis, and print the report URL. Exit codes: 0 ok, 2 --fail-on threshold reached, 1 error.
/// </summary>
public static class ScanCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var path = Path.GetFullPath(Opt(args, "--path") ?? ".");
        if (!Directory.Exists(path)) { Console.Error.WriteLine($"error: --path '{path}' is not a directory."); return 1; }
        var cfg = ClientConfig.Load(args, path);
        var jsonOut = args.Contains("--json");
        var dryRun = args.Contains("--dry-run");
        var log = jsonOut ? Console.Error : Console.Out;

        // ---- 1. Build the digest locally ----
        var ingest = new RepoIngest(cfg.IngestOptions(Path.Combine(Path.GetTempPath(), "secureflow-cli")));
        var progress = new SyncProgress(m => log.WriteLine("  " + m));
        log.WriteLine($"SecureFlow {Version} - scanning {path}");
        log.WriteLine($"Server: {cfg.Server}   (change with --server, SECUREFLOW_SERVER or ~/.secureflow/config.yml)");
        log.WriteLine();
        log.WriteLine("[1/3] Reading the project on this machine (nothing is compiled or executed; secret values are redacted)");
        RepoDigest digest;
        try { digest = await ingest.BuildAsync(path, null, progress, CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine("error: could not read the project: " + ex.Message); return 1; }
        ingest.Redact(digest, progress);

        var (repoName, remote) = await GitIdentityAsync(path);
        digest.RepoUrl = remote ?? repoName;
        var branch = digest.Branch is null or "HEAD" ? (Environment.GetEnvironmentVariable("GITHUB_HEAD_REF") ?? Environment.GetEnvironmentVariable("BRANCH_NAME") ?? digest.Commit ?? "local") : digest.Branch;
        digest.Branch = branch;
        var key = cfg.Get("projectKey") ?? $"{repoName}@{branch}";
        var name = cfg.Get("name") ?? repoName;

        if (args.Contains("--save-digest") && Opt(args, "--save-digest") is { } digestFile)
        {
            await File.WriteAllTextAsync(digestFile, JsonSerializer.Serialize(digest, Json));
            log.WriteLine($"  Digest written to {digestFile}");
        }
        if (dryRun)
        {
            log.WriteLine();
            log.WriteLine($"Dry run: would upload {digest.Files.Count} files ({digest.IncludedBytes / 1000}k chars) and {digest.Dependencies.Count} dependencies as '{key}' to {cfg.Server}.");
            foreach (var f in digest.Files) log.WriteLine($"  {f.Content.Length,7}  {f.Path}{(f.Truncated ? " (truncated)" : "")}");
            return 0;
        }

        // ---- 2. Upload ----
        using var http = CreateClient(cfg);
        log.WriteLine();
        log.WriteLine($"[2/3] Uploading {digest.Files.Count} files and {digest.Dependencies.Count} dependency versions to {cfg.Server} as '{key}'");
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsJsonAsync("api/scans", new
            {
                projectKey = key, name, group = cfg.Get("group"), branch, commit = digest.Commit,
                source = $"secureflow-cli {Environment.UserName}@{Environment.MachineName}", digest,
            }, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"error: cannot reach SecureFlow at {cfg.Server}: {ex.Message}");
            Console.Error.WriteLine("       Start it locally with .\\scripts\\run.ps1, or point --server / SECUREFLOW_SERVER at the shared instance.");
            return 1;
        }

        ScanSummary? summary;
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            summary = await res.Content.ReadFromJsonAsync<ScanSummary>(Json);
            log.WriteLine("  A scan of this project is already running on the server; following it instead.");
        }
        else if (!res.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error: server returned {(int)res.StatusCode} {res.ReasonPhrase}: {await res.Content.ReadAsStringAsync()}");
            if (res.StatusCode == HttpStatusCode.Unauthorized) Console.Error.WriteLine("       Set --token or SECUREFLOW_TOKEN (ask the SecureFlow server owner for one).");
            return 1;
        }
        else summary = await res.Content.ReadFromJsonAsync<ScanSummary>(Json);
        if (summary is null) { Console.Error.WriteLine("error: empty response from server."); return 1; }

        log.WriteLine($"  Report: {summary.Url}");
        if (!cfg.GetBool("wait"))
        {
            if (jsonOut) Console.WriteLine(JsonSerializer.Serialize(summary, Json));
            return 0;
        }

        // ---- 3. Wait ----
        var timeout = TimeSpan.FromSeconds(cfg.GetInt("timeoutSeconds") ?? 900);
        var poll = TimeSpan.FromSeconds(Math.Max(1, cfg.GetInt("pollSeconds") ?? 3));
        log.WriteLine();
        log.WriteLine("[3/3] Server is analysing: architecture model (AI), resilience + STRIDE rules, CVE check of dependencies");
        var sw = Stopwatch.StartNew();
        string? lastLog = null, lastStatus = null;
        while (summary.Status is not ("Ready" or "Error"))
        {
            if (sw.Elapsed > timeout)
            {
                Console.Error.WriteLine($"error: scan did not finish within {timeout.TotalSeconds:F0}s; it continues on the server: {summary.Url}");
                return 1;
            }
            await Task.Delay(poll);
            try { summary = await http.GetFromJsonAsync<ScanSummary>($"api/projects/{summary.Id}/summary", Json) ?? summary; }
            catch (HttpRequestException ex) { log.WriteLine("  (status check failed, retrying: " + ex.Message + ")"); continue; }
            if (summary.Status != lastStatus) { log.WriteLine($"  [{summary.Status}]"); lastStatus = summary.Status; }
            if (summary.LastLog is { } l && l != lastLog) { log.WriteLine("  " + l); lastLog = l; }
        }

        // ---- 4. Report ----
        if (summary.Status == "Error")
        {
            Console.Error.WriteLine($"error: scan failed on the server: {summary.Error}");
            Console.Error.WriteLine($"       Details: {summary.Url}");
            return 1;
        }

        if (cfg.Get("out") is { } outFile)
        {
            try
            {
                var md = await http.GetStringAsync(summary.ReportMarkdownUrl);
                await File.WriteAllTextAsync(outFile, md);
                log.WriteLine($"  Markdown report saved to {outFile}");
            }
            catch (Exception ex) { Console.Error.WriteLine("warning: could not save the report: " + ex.Message); }
        }

        var open = summary.OpenBySeverity ?? new();
        if (jsonOut) Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        else
        {
            Console.WriteLine();
            Console.WriteLine($"{summary.Name}  (scan #{summary.ScanCount}, {summary.Branch}{(summary.Commit is null ? "" : " @ " + summary.Commit)})");
            if (summary.ArchitectureAnalysed)
                Console.WriteLine($"  Resilience {summary.Score?.Resilience} ({summary.Score?.ResilienceGrade})   Security {summary.Score?.Security} ({summary.Score?.SecurityGrade})");
            else
                Console.WriteLine("  Architecture not analysed (the server has no AI provider configured); dependency CVEs only.");
            Console.WriteLine($"  Open findings: {summary.OpenFindings}" + (open.Count > 0 ? "  (" + string.Join(", ", open.OrderByDescending(kv => Sev(kv.Key)).Select(kv => $"{kv.Value} {kv.Key}")) + ")" : "")
                + $"   of which CVEs: {summary.OpenVulnerabilities}");
            Console.WriteLine($"  Report: {summary.Url}");
        }

        if (cfg.GetBool("open")) TryOpen(summary.Url);

        var failOn = cfg.Get("failOn") ?? "none";
        if (!failOn.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<Severity>(failOn, true, out var threshold)) { Console.Error.WriteLine($"error: unknown --fail-on '{failOn}' (none|low|medium|high|critical)."); return 1; }
            var breaching = open.Where(kv => Sev(kv.Key) >= threshold).Sum(kv => kv.Value);
            if (breaching > 0)
            {
                Console.Error.WriteLine($"SecureFlow gate: {breaching} open finding(s) at or above {threshold}. Failing (exit 2).");
                return 2;
            }
        }
        return 0;
    }

    public static string Version =>
        typeof(ScanCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(ScanCommand).Assembly.GetName().Version?.ToString() ?? "dev";

    public static int PrintConfig(IReadOnlyList<string> args)
    {
        var path = Path.GetFullPath(Opt(args, "--path") ?? ".");
        ClientConfig.Load(args, path).Print(Console.Out);
        return 0;
    }

    private static Severity Sev(string s) => Enum.TryParse<Severity>(s, true, out var v) ? v : Severity.Info;

    private static HttpClient CreateClient(ClientConfig cfg)
    {
        var handler = new HttpClientHandler();
        if (cfg.GetBool("insecureSkipTlsVerify"))
        {
            Console.Error.WriteLine("warning: TLS certificate validation is disabled (insecureSkipTlsVerify).");
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        var http = new HttpClient(handler) { BaseAddress = new Uri(cfg.Server + "/"), Timeout = TimeSpan.FromSeconds(120) };
        if (cfg.Get("token") is { } token) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("secureflow-cli", typeof(ScanCommand).Assembly.GetName().Version?.ToString() ?? "1.0"));
        return http;
    }

    /// <summary>Repo name (plus sub-folder for monorepo modules) and a credential-free remote URL.</summary>
    private static async Task<(string Name, string? Remote)> GitIdentityAsync(string path)
    {
        var top = (await Git(path, "rev-parse", "--show-toplevel"))?.Trim();
        var remote = (await Git(path, "remote", "get-url", "origin"))?.Trim();
        if (remote is { Length: > 0 }) remote = System.Text.RegularExpressions.Regex.Replace(remote, @"//[^/@]+@", "//"); // strip user:token@
        else remote = null;

        var baseName = remote is not null
            ? remote.TrimEnd('/').Split('/', ':').Last().Replace(".git", "")
            : Path.GetFileName(top is { Length: > 0 } ? top : path.TrimEnd(Path.DirectorySeparatorChar));
        if (top is { Length: > 0 })
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(top), path).Replace('\\', '/');
            if (rel != "." && !rel.StartsWith("..")) return ($"{baseName}/{rel}", remote);
        }
        return (baseName, remote);
    }

    private static async Task<string?> Git(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static void TryOpen(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine("warning: could not open a browser: " + ex.Message); }
    }

    private static string? Opt(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool, which reorders console lines; this reports inline.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    public sealed class ScanSummary
    {
        public string Id { get; set; } = "";
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public int ScanCount { get; set; }
        public string? Branch { get; set; }
        public string? Commit { get; set; }
        public string Url { get; set; } = "";
        public string ReportMarkdownUrl { get; set; } = "";
        public string? StatusUrl { get; set; }
        public ScoreInfo? Score { get; set; }
        public Dictionary<string, int>? OpenBySeverity { get; set; }
        public int OpenFindings { get; set; }
        public int OpenVulnerabilities { get; set; }
        public bool AiReview { get; set; }
        public bool ArchitectureAnalysed { get; set; }
        public string? LastLog { get; set; }
    }

    public sealed class ScoreInfo
    {
        public int Resilience { get; set; }
        public string? ResilienceGrade { get; set; }
        public int Security { get; set; }
        public string? SecurityGrade { get; set; }
    }
}
