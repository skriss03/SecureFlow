using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SecureFlow.Ai;

namespace SecureFlow.Ingest;

/// <summary>
/// Clones a repository (shallow) and builds a <see cref="RepoDigest"/>: a directory tree plus the files that
/// describe deployment shape, configuration and service wiring, within a character budget. Deterministic and
/// provider-neutral; the AI extractor turns the digest into a model.
/// </summary>
public sealed class RepoIngest
{
    private readonly string _workDir;

    public RepoIngest(string workDir)
    {
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>Total characters of file content included (roughly 4 chars per token).</summary>
    public int BudgetChars { get; init; } = 480_000;
    public int PerFileCapChars { get; init; } = 24_000;
    public int MaxFiles { get; init; } = 140;

    private static readonly string[] SkipDirs =
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".idea", "packages", "vendor", "target",
        "__pycache__", ".venv", "venv", ".terraform", "coverage", "TestResults", ".next", "out",
    };

    private static readonly string[] BinaryExt =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".pdf", ".zip", ".gz", ".tar", ".dll", ".exe", ".pdb",
        ".jar", ".class", ".so", ".dylib", ".woff", ".woff2", ".ttf", ".mp4", ".mp3", ".lock", ".snap",
    };

    // Ordered by priority. Earlier groups are included first when the budget is tight.
    private static readonly (string Label, Regex Pattern)[] Priorities =
    {
        ("deployment", Rx(@"(^|/)(Dockerfile[^/]*|docker-compose[^/]*\.ya?ml|compose[^/]*\.ya?ml|[^/]+\.tf|[^/]+\.tfvars|[^/]+\.bicep|[^/]+\.bicepparam|azuredeploy[^/]*\.json|serverless\.ya?ml|Chart\.yaml|values[^/]*\.ya?ml)$|(^|/)(k8s|kubernetes|manifests|helm|charts|deploy|deployment|infra|infrastructure|terraform|iac)/.*\.(ya?ml|json|tf|bicep)$")),
        ("pipelines", Rx(@"(^|/)\.github/workflows/[^/]+\.ya?ml$|(^|/)azure-pipelines[^/]*\.ya?ml$|(^|/)\.gitlab-ci\.ya?ml$|(^|/)Jenkinsfile$")),
        ("config", Rx(@"(^|/)(appsettings[^/]*\.json|\.env[^/]*|[^/]*\.env|application[^/]*\.(ya?ml|properties)|config\.(json|ya?ml)|ocelot[^/]*\.json|nginx[^/]*\.conf|Caddyfile|haproxy\.cfg|launchSettings\.json)$|(^|/)config/[^/]+\.(json|ya?ml)$")),
        ("entrypoints", Rx(@"(^|/)(Program\.cs|Startup\.cs|[^/]+\.csproj|package\.json|requirements\.txt|pyproject\.toml|go\.mod|pom\.xml|build\.gradle(\.kts)?|main\.(go|py)|app\.(py|js|ts)|server\.(js|ts)|index\.(js|ts))$")),
        ("docs", Rx(@"(^|/)(README[^/]*|ARCHITECTURE[^/]*|DESIGN[^/]*)\.(md|txt)$|(^|/)docs/[^/]+\.md$")),
    };

    // Source files are included only if they mention service wiring, so we spend budget on what matters.
    private static readonly Regex WiringSignal = new(
        @"AddHttpClient|HttpClient|BaseAddress|ConnectionString|AddDbContext|UseSqlServer|UseNpgsql|UseMySql|MongoClient|" +
        @"Redis|StackExchange|ServiceBus|RabbitMQ|Kafka|EventHub|Sqs|Sns|MassTransit|AddAuthentication|JwtBearer|" +
        @"MapHealthChecks|AddHealthChecks|Polly|Resilience|CircuitBreaker|RateLimiter|AddRateLimiter|BlobServiceClient|" +
        @"S3Client|KeyVault|SecretClient|ApplicationInsights|OpenTelemetry|Serilog|requests\.(get|post)|fetch\(|axios|" +
        @"createClient|Pool\(|psycopg|pymongo|boto3|@azure/|aws-sdk",
        RegexOptions.Compiled);

    private static readonly Regex SourceExt = new(@"\.(cs|ts|js|py|go|java|kt|rb|php|yaml|yml|json|toml|ini|conf)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<RepoDigest> BuildAsync(string urlOrPath, string? branch, IProgress<string>? progress, CancellationToken ct)
    {
        string root;
        string? commit = null;
        var isLocal = Directory.Exists(urlOrPath);

        if (isLocal)
        {
            root = Path.GetFullPath(urlOrPath);
            progress?.Report($"Using local repository at {root}.");
        }
        else
        {
            ValidateUrl(urlOrPath);
            root = Path.Combine(_workDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
            progress?.Report($"Cloning {urlOrPath}{(branch is null ? "" : $" ({branch})")}...");
            // core.longpaths: without it, checkout fails on Windows for repos with paths over MAX_PATH
            // (gitea's mock fixtures, for one). Harmless everywhere else.
            var args = new List<string> { "-c", "core.longpaths=true", "clone", "--depth", "1", "--quiet" };
            if (!string.IsNullOrWhiteSpace(branch)) { args.Add("--branch"); args.Add(branch); }
            args.Add(urlOrPath); args.Add(root);
            var (code, _, err) = await RunGitAsync(args, _workDir, ct);
            if (code != 0) throw new InvalidOperationException($"git clone failed: {err.Trim()}");
        }

        var (rc, headOut, _) = await RunGitAsync(new[] { "rev-parse", "--short", "HEAD" }, root, ct);
        if (rc == 0) commit = headOut.Trim();
        if (branch is null)
        {
            var (bc, bOut, _) = await RunGitAsync(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, root, ct);
            if (bc == 0) branch = bOut.Trim();
        }

        progress?.Report("Scanning files...");
        var all = EnumerateFiles(root).ToList();
        var digest = new RepoDigest
        {
            RepoUrl = urlOrPath,
            Branch = branch,
            Commit = commit,
            TotalFilesInRepo = all.Count,
            Tree = BuildTree(root, all),
        };

        var chosen = new List<(string rel, string label)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, pattern) in Priorities)
            foreach (var rel in all.Where(r => pattern.IsMatch(r)).OrderBy(r => r.Count(c => c == '/')).ThenBy(r => r))
                if (seen.Add(rel)) chosen.Add((rel, label));

        // Source files with wiring signals, smallest first (they are usually the config-ish ones).
        var sourceCandidates = all.Where(r => !seen.Contains(r) && SourceExt.IsMatch(r))
            .Select(r => new FileInfo(Path.Combine(root, r)))
            .Where(fi => fi.Length < 200_000)
            .OrderBy(fi => fi.Length)
            .ToList();
        var wiringHits = 0;
        foreach (var fi in sourceCandidates)
        {
            if (wiringHits >= 40) break;
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = await File.ReadAllTextAsync(fi.FullName, ct); } catch { continue; }
            if (!WiringSignal.IsMatch(text)) continue;
            var rel = Path.GetRelativePath(root, fi.FullName).Replace('\\', '/');
            if (seen.Add(rel)) { chosen.Add((rel, "wiring")); wiringHits++; }
        }

        long used = 0;
        foreach (var (rel, label) in chosen)
        {
            if (digest.Files.Count >= MaxFiles) { digest.SkippedNotes.Add($"File limit reached ({MaxFiles}); remaining {label} files omitted."); break; }
            var full = Path.Combine(root, rel);
            string content;
            try { content = await File.ReadAllTextAsync(full, ct); } catch { continue; }
            if (content.Contains('\0')) continue; // binary masquerading as text
            var truncated = false;
            if (content.Length > PerFileCapChars) { content = content[..PerFileCapChars] + "\n... [truncated]"; truncated = true; }
            if (used + content.Length > BudgetChars)
            {
                digest.SkippedNotes.Add($"Character budget reached; {label} file {rel} and later files omitted.");
                break;
            }
            used += content.Length;
            digest.Files.Add(new RepoFile { Path = rel, Content = content, Truncated = truncated });
        }
        digest.IncludedBytes = used;
        progress?.Report($"Digest ready: {digest.Files.Count} of {all.Count} files, {used / 1000}k characters, commit {commit ?? "n/a"}.");

        if (!isLocal)
        {
            try { DeleteReadOnly(root); } catch { /* temp dir; best effort */ }
        }
        return digest;
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
            throw new ArgumentException("Repository must be a local directory or an http(s)/ssh git URL.");
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { continue; }
            foreach (var e in entries)
            {
                var name = Path.GetFileName(e);
                if (Directory.Exists(e))
                {
                    if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push(e);
                }
                else
                {
                    if (BinaryExt.Contains(Path.GetExtension(e), StringComparer.OrdinalIgnoreCase)) continue;
                    yield return Path.GetRelativePath(root, e).Replace('\\', '/');
                }
            }
        }
    }

    private static string BuildTree(string root, List<string> files)
    {
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(root)).Append("/\n");
        var byDir = files.GroupBy(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            .OrderBy(g => g.Key).ToList();
        var lines = 0;
        foreach (var g in byDir)
        {
            if (lines > 350) { sb.Append("... (tree truncated)\n"); break; }
            var depth = g.Key.Length == 0 ? 0 : g.Key.Count(c => c == '/') + 1;
            if (depth > 5) continue;
            if (g.Key.Length > 0) { sb.Append(new string(' ', depth * 2)).Append(g.Key[(g.Key.LastIndexOf('/') + 1)..]).Append("/\n"); lines++; }
            var shown = 0;
            foreach (var f in g.OrderBy(x => x))
            {
                if (shown++ >= 25) { sb.Append(new string(' ', (depth + 1) * 2)).Append($"... {g.Count() - 25} more\n"); lines++; break; }
                sb.Append(new string(' ', (depth + 1) * 2)).Append(f[(f.LastIndexOf('/') + 1)..]).Append('\n');
                lines++;
            }
        }
        return sb.ToString();
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitAsync(IEnumerable<string> args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never hang waiting for credentials
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git is not installed or not on PATH.");
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await so, await se);
    }

    private static void DeleteReadOnly(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
