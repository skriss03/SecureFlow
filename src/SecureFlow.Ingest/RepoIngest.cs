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
    private readonly RepoIngestOptions _options;
    private readonly string _workDir;
    private readonly (string Label, Regex Pattern)[] _priorities;
    private readonly Regex _wiringSignal;
    private readonly HashSet<string> _skipDirs;
    private readonly HashSet<string> _binaryExt;

    public RepoIngest(string workDir) : this(new RepoIngestOptions { WorkDir = workDir }) { }

    public RepoIngest(RepoIngestOptions options)
    {
        _options = options;
        _workDir = string.IsNullOrWhiteSpace(options.WorkDir) ? Path.Combine(Path.GetTempPath(), "secureflow") : options.WorkDir;
        Directory.CreateDirectory(_workDir);

        _skipDirs = new HashSet<string>(options.EffectiveSkipDirs, StringComparer.OrdinalIgnoreCase);
        _binaryExt = new HashSet<string>(options.EffectiveBinaryExtensions, StringComparer.OrdinalIgnoreCase);
        _priorities = options.ExtraPriorityPatterns.Length == 0
            ? DefaultPriorities
            : DefaultPriorities.Take(3).Append(("custom", Rx(string.Join("|", options.ExtraPriorityPatterns.Select(p => $"(?:{p})"))))).Concat(DefaultPriorities.Skip(3)).ToArray();
        _wiringSignal = options.ExtraWiringSignals.Length == 0
            ? DefaultWiringSignal
            : new Regex(DefaultWiringSignal + "|" + string.Join("|", options.ExtraWiringSignals.Select(p => $"(?:{p})")), RegexOptions.Compiled);
    }

    public RepoIngestOptions Options => _options;

    // Ordered by priority. Earlier groups are included first when the budget is tight.
    private static readonly (string Label, Regex Pattern)[] DefaultPriorities =
    {
        ("deployment", Rx(@"(^|/)(Dockerfile[^/]*|Containerfile|docker-compose[^/]*\.ya?ml|compose[^/]*\.ya?ml|[^/]+\.tf|[^/]+\.tfvars|[^/]+\.bicep|[^/]+\.bicepparam|azuredeploy[^/]*\.json|serverless\.ya?ml|Chart\.yaml|values[^/]*\.ya?ml|Procfile|manifest\.ya?ml)$|(^|/)(k8s|kubernetes|manifests|helm|charts|deploy|deployment|infra|infrastructure|terraform|iac|openshift|ocp)/.*\.(ya?ml|json|tf|bicep)$")),
        ("pipelines", Rx(@"(^|/)\.github/workflows/[^/]+\.ya?ml$|(^|/)azure-pipelines[^/]*\.ya?ml$|(^|/)\.gitlab-ci\.ya?ml$|(^|/)Jenkinsfile$")),
        // Spring (application/bootstrap), .NET (appsettings), Angular (environment*.ts, angular.json, proxy.conf),
        // React/Next/Vite (vite.config, next.config), Django (settings.py, settings/*.py), generic .env and gateways.
        ("config", Rx(@"(^|/)(appsettings[^/]*\.json|\.env[^/]*|[^/]*\.env|(application|bootstrap)[^/]*\.(ya?ml|properties)|config\.(json|ya?ml)|ocelot[^/]*\.json|nginx[^/]*\.conf|Caddyfile|haproxy\.cfg|launchSettings\.json|environment[^/]*\.ts|angular\.json|proxy\.conf\.(json|js)|(vite|next)\.config\.[mc]?[jt]s|settings\.py)$|(^|/)config/[^/]+\.(json|ya?ml)$|(^|/)settings/[^/]+\.py$")),
        ("entrypoints", Rx(@"(^|/)(Program\.cs|Startup\.cs|[^/]+\.csproj|Directory\.Packages\.props|package\.json|requirements[^/]*\.txt|pyproject\.toml|Pipfile|setup\.py|go\.mod|pom\.xml|(build|settings)\.gradle(\.kts)?|libs\.versions\.toml|main\.(go|py|ts)|app\.(py|js|ts)|server\.(js|ts)|index\.(js|ts)|manage\.py|wsgi\.py|asgi\.py|urls\.py|nest-cli\.json|[^/]*Application\.(java|kt))$")),
        // Docs, plus architecture diagrams kept as code (Mermaid, PlantUML/C4, Structurizr): the AI reads them as context.
        ("docs", Rx(@"(^|/)(README[^/]*|ARCHITECTURE[^/]*|DESIGN[^/]*)\.(md|txt)$|(^|/)docs/[^/]+\.md$|(^|/)[^/]+\.(mmd|mermaid|puml|plantuml|dsl)$")),
    };

    // Source files are included only if they mention service wiring, so we spend budget on what matters.
    private static readonly Regex DefaultWiringSignal = new(
        // .NET
        @"AddHttpClient|HttpClient|BaseAddress|ConnectionString|AddDbContext|UseSqlServer|UseNpgsql|UseMySql|MongoClient|" +
        @"Redis|StackExchange|ServiceBus|RabbitMQ|Kafka|EventHub|Sqs|Sns|MassTransit|AddAuthentication|JwtBearer|" +
        @"MapHealthChecks|AddHealthChecks|Polly|Resilience|CircuitBreaker|RateLimiter|AddRateLimiter|BlobServiceClient|" +
        @"S3Client|KeyVault|SecretClient|ApplicationInsights|OpenTelemetry|Serilog|" +
        // Java / Spring
        @"RestTemplate|WebClient|@FeignClient|KafkaTemplate|@KafkaListener|@RabbitListener|JmsTemplate|JdbcTemplate|" +
        @"spring\.datasource|@EnableWebSecurity|SecurityFilterChain|@Retry\b|@Bulkhead|@TimeLimiter|@Cacheable|" +
        // Python / Django
        @"requests\.(get|post)|httpx|aiohttp|FastAPI|Flask\(|SQLAlchemy|create_engine|celery|psycopg|pymongo|boto3|" +
        @"DATABASES|CACHES|CELERY_|ALLOWED_HOSTS|SECRET_KEY|django_redis|" +
        // JavaScript / TypeScript (Angular, React, Node)
        @"fetch\(|axios|apiUrl|HttpInterceptor|VITE_|REACT_APP_|NEXT_PUBLIC_|mongoose|kafkajs|ioredis|amqplib|" +
        @"createClient|Pool\(|@azure/|aws-sdk",
        RegexOptions.Compiled);

    private static readonly Regex SourceExt = new(@"\.(cs|ts|tsx|js|jsx|py|go|java|kt|rb|php|yaml|yml|json|toml|ini|conf)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        foreach (var (label, pattern) in _priorities)
            foreach (var rel in all.Where(r => pattern.IsMatch(r)).OrderBy(r => r.Count(c => c == '/')).ThenBy(r => r))
                if (seen.Add(rel)) chosen.Add((rel, label));

        // Source files with wiring signals, smallest first (they are usually the config-ish ones).
        var sourceCandidates = all.Where(r => !seen.Contains(r) && SourceExt.IsMatch(r) && !ManifestParser.IsManifest(r))
            .Select(r => new FileInfo(Path.Combine(root, r)))
            .Where(fi => fi.Length < _options.MaxSourceFileBytes)
            .OrderBy(fi => fi.Length)
            .ToList();
        var wiringHits = 0;
        foreach (var fi in sourceCandidates)
        {
            if (wiringHits >= _options.MaxWiringFiles) break;
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = await File.ReadAllTextAsync(fi.FullName, ct); } catch { continue; }
            if (!_wiringSignal.IsMatch(text)) continue;
            var rel = Path.GetRelativePath(root, fi.FullName).Replace('\\', '/');
            if (seen.Add(rel)) { chosen.Add((rel, "wiring")); wiringHits++; }
        }

        long used = 0;
        foreach (var (rel, label) in chosen)
        {
            if (digest.Files.Count >= _options.MaxFiles) { digest.SkippedNotes.Add($"File limit reached ({_options.MaxFiles}); remaining {label} files omitted."); break; }
            var full = Path.Combine(root, rel);
            string content;
            try { content = await File.ReadAllTextAsync(full, ct); } catch { continue; }
            if (content.Contains('\0')) continue; // binary masquerading as text
            var truncated = false;
            if (content.Length > _options.PerFileCapChars) { content = content[.._options.PerFileCapChars] + "\n... [truncated]"; truncated = true; }
            if (used + content.Length > _options.BudgetChars)
            {
                digest.SkippedNotes.Add($"Character budget reached; {label} file {rel} and later files omitted.");
                break;
            }
            used += content.Length;
            digest.Files.Add(new RepoFile { Path = rel, Content = content, Truncated = truncated });
        }
        digest.IncludedBytes = used;

        if (_options.ResolveDependencies)
        {
            // Read every manifest and lockfile in full (not just the ones that fit the AI budget); only the
            // resolved (ecosystem, name, version) list is kept, so lockfiles never bloat the digest.
            var deps = new List<DependencyRef>();
            foreach (var rel in all.Where(ManifestParser.IsManifest))
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(Path.Combine(root, rel));
                if (!fi.Exists || fi.Length > _options.MaxManifestBytes) continue;
                string text;
                try { text = await File.ReadAllTextAsync(fi.FullName, ct); } catch { continue; }
                deps.AddRange(ManifestParser.Parse(rel, text));
            }
            digest.Dependencies = ManifestParser.Distinct(deps);
            if (digest.Dependencies.Count > 0)
                progress?.Report($"Resolved {digest.Dependencies.Count} pinned dependencies ({string.Join(", ", digest.Dependencies.GroupBy(d => d.Ecosystem).Select(g => $"{g.Key} {g.Count()}"))}).");
        }

        progress?.Report($"Digest ready: {digest.Files.Count} of {all.Count} files, {used / 1000}k characters, commit {commit ?? "n/a"}.");

        if (!isLocal)
        {
            try { DeleteReadOnly(root); } catch { /* temp dir; best effort */ }
        }
        return digest;
    }

    /// <summary>
    /// Applies <see cref="RepoIngestOptions.Redaction"/> to a built digest. Callers decide when: the CLI
    /// always does before upload; the server only when configured, since its own clones never leave it
    /// (and redacting changes the content hash the AI replay cache is keyed on).
    /// </summary>
    public int Redact(RepoDigest digest, IProgress<string>? progress)
    {
        var count = new SecretRedactor(_options.Redaction).Redact(digest);
        if (count > 0) progress?.Report($"Redacted {count} secret value(s) from configuration and code.");
        return count;
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
            throw new ArgumentException("Repository must be a local directory or an http(s)/ssh git URL.");
    }

    private IEnumerable<string> EnumerateFiles(string root)
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
                    if (_skipDirs.Contains(name)) continue;
                    stack.Push(e);
                }
                else
                {
                    if (_binaryExt.Contains(Path.GetExtension(e))) continue;
                    yield return Path.GetRelativePath(root, e).Replace('\\', '/');
                }
            }
        }
    }

    private string BuildTree(string root, List<string> files)
    {
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(root)).Append("/\n");
        var byDir = files.GroupBy(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            .OrderBy(g => g.Key).ToList();
        var lines = 0;
        foreach (var g in byDir)
        {
            if (lines > _options.MaxTreeLines) { sb.Append("... (tree truncated)\n"); break; }
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
