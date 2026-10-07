using System.Text;
using System.Text.RegularExpressions;
using SecureFlow.Ingest;

namespace SecureFlow.Cli;

/// <summary>
/// Client settings for `secureflow scan`, resolved per key, highest precedence first:
///   1. command-line flag       --project-key x        (kebab-case of the key; nested keys joined with '-')
///   2. environment variable    SECUREFLOW_PROJECT_KEY (SECUREFLOW_ + upper snake case)
///   3. repo file               .secureflow.yml        (searched from --path up to the git root)
///   4. user file               ~/.secureflow/config.yml, or the file named by SECUREFLOW_CONFIG
///   5. built-in default
/// Moving from a local server to a shared one is therefore one env var or one line in the user file.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>Every supported key with its default and description; the single source of truth for `secureflow config`.</summary>
    public static readonly (string Key, string? Default, string Help)[] Keys =
    {
        ("server", "http://localhost:5180", "SecureFlow server base URL"),
        ("token", null, "API token, sent as 'Authorization: Bearer <token>' (required when the server has auth enabled)"),
        ("group", null, "Team/group name or id; shows the project on that group's dashboard"),
        ("projectKey", null, "Stable project identity; default '<repo>[/<subdir>]@<branch>'"),
        ("name", null, "Display name; default is the repo or folder name"),
        ("failOn", "none", "Exit 2 if an open finding is at or above: none|low|medium|high|critical"),
        ("wait", "true", "Wait for the scan to finish and print the result"),
        ("timeoutSeconds", "900", "Give up waiting after this many seconds"),
        ("pollSeconds", "3", "Status polling interval"),
        ("out", null, "Also save the Markdown report to this file"),
        ("open", "false", "Open the report in the default browser when done"),
        ("insecureSkipTlsVerify", "false", "Accept any TLS certificate (only for internal test servers)"),
        ("ingest.budgetChars", null, "Max characters of file content to upload (server default otherwise)"),
        ("ingest.perFileCapChars", null, "Max characters per file"),
        ("ingest.maxFiles", null, "Max files in the digest"),
        ("ingest.extraSkipDirs", null, "Extra directory names to skip (list)"),
        ("ingest.extraPriorityPatterns", null, "Extra path regexes to always include (list)"),
        ("ingest.extraWiringSignals", null, "Extra source regexes that mark service wiring (list)"),
        ("ingest.resolveDependencies", null, "Resolve manifests/lockfiles for CVE scanning (true/false)"),
        ("redaction.enabled", "true", "Redact secret values before upload"),
        ("redaction.keyPattern", null, "Regex of key names whose values are redacted"),
    };

    private readonly Dictionary<string, (string Value, string Source)> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<string> Items, string Source)> _lists = new(StringComparer.OrdinalIgnoreCase);

    public string? RepoFile { get; private set; }
    public string? UserFile { get; private set; }

    public static ClientConfig Load(IReadOnlyList<string> args, string scanPath)
    {
        var c = new ClientConfig();
        // Lowest precedence first; later layers overwrite.
        foreach (var (k, d, _) in Keys) if (d is not null) c._values[k] = (d, "default");

        c.UserFile = Environment.GetEnvironmentVariable("SECUREFLOW_CONFIG") is { Length: > 0 } explicitFile
            ? explicitFile
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".secureflow", "config.yml");
        if (File.Exists(c.UserFile)) c.ApplyFile(c.UserFile, "user file " + c.UserFile);

        c.RepoFile = FindRepoFile(scanPath);
        if (c.RepoFile is not null) c.ApplyFile(c.RepoFile, "repo file " + c.RepoFile);

        foreach (var (k, _, _) in Keys)
        {
            var env = Environment.GetEnvironmentVariable(EnvName(k));
            if (string.IsNullOrEmpty(env)) continue;
            c._values[k] = (env, "env " + EnvName(k));
            if (IsList(k)) c._lists[k] = (SplitList(env), "env " + EnvName(k));
        }

        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var flag = args[i][2..];
            var key = Keys.Select(x => x.Key).FirstOrDefault(k => FlagName(k).Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (key is null) continue;
            var isBool = Keys.First(x => x.Key == key).Default is "true" or "false";
            string value;
            if (isBool && (i + 1 >= args.Count || args[i + 1].StartsWith("--"))) value = "true";
            else if (i + 1 < args.Count) value = args[++i];
            else continue;
            c._values[key] = (value, "flag --" + FlagName(key));
            if (IsList(key)) c._lists[key] = (SplitList(value), "flag --" + FlagName(key));
        }
        // Convenience negations.
        if (args.Contains("--no-wait")) c._values["wait"] = ("false", "flag --no-wait");
        if (args.Contains("--no-redact")) c._values["redaction.enabled"] = ("false", "flag --no-redact");
        return c;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) && v.Value.Length > 0 ? v.Value : null;
    public string Source(string key) => _values.TryGetValue(key, out var v) ? v.Source : "unset";
    public bool GetBool(string key) => bool.TryParse(Get(key), out var b) && b;
    public int? GetInt(string key) => int.TryParse(Get(key), out var n) ? n : null;
    public string[]? GetList(string key) => _lists.TryGetValue(key, out var l) ? l.Items.ToArray() : null;

    public string Server => (Get("server") ?? "http://localhost:5180").TrimEnd('/');

    /// <summary>Server-equivalent ingest options with any client overrides applied.</summary>
    public RepoIngestOptions IngestOptions(string workDir)
    {
        var o = new RepoIngestOptions { WorkDir = workDir };
        if (GetInt("ingest.budgetChars") is { } b) o.BudgetChars = b;
        if (GetInt("ingest.perFileCapChars") is { } pf) o.PerFileCapChars = pf;
        if (GetInt("ingest.maxFiles") is { } mf) o.MaxFiles = mf;
        if (GetList("ingest.extraSkipDirs") is { } sd) o.ExtraSkipDirs = sd;
        if (GetList("ingest.extraPriorityPatterns") is { } pp) o.ExtraPriorityPatterns = pp;
        if (GetList("ingest.extraWiringSignals") is { } ws) o.ExtraWiringSignals = ws;
        if (Get("ingest.resolveDependencies") is { } rd && bool.TryParse(rd, out var rdb)) o.ResolveDependencies = rdb;
        o.Redaction.Enabled = !bool.TryParse(Get("redaction.enabled"), out var re) || re;
        if (Get("redaction.keyPattern") is { } kp) o.Redaction.KeyPattern = kp;
        return o;
    }

    public void Print(TextWriter w)
    {
        w.WriteLine($"Repo file: {RepoFile ?? "(none found)"}");
        w.WriteLine($"User file: {UserFile}{(UserFile is not null && File.Exists(UserFile) ? "" : " (not present)")}");
        w.WriteLine();
        foreach (var (k, _, help) in Keys)
        {
            var value = IsList(k) && GetList(k) is { } l ? "[" + string.Join(", ", l) + "]" : Get(k);
            if (k == "token" && value is not null) value = value.Length <= 4 ? "****" : value[..2] + new string('*', Math.Min(12, value.Length - 4)) + value[^2..];
            w.WriteLine($"  {k,-28} {(value ?? "-"),-34} {Source(k)}");
        }
        w.WriteLine();
        w.WriteLine("Precedence: --flag > SECUREFLOW_* env var > repo .secureflow.yml > user ~/.secureflow/config.yml > default.");
        w.WriteLine("Env var names: " + string.Join(", ", Keys.Take(6).Select(k => EnvName(k.Key))) + ", ...");
    }

    // ---------- naming ----------

    public static string EnvName(string key) =>
        "SECUREFLOW_" + Regex.Replace(key.Replace('.', '_'), "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();

    public static string FlagName(string key) =>
        Regex.Replace(key.Replace('.', '-'), "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant();

    private static bool IsList(string key) => key.StartsWith("ingest.extra", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---------- files ----------

    private static string? FindRepoFile(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            foreach (var name in new[] { ".secureflow.yml", ".secureflow.yaml" })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate)) return candidate;
            }
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) break; // stop at the repo root
            dir = dir.Parent;
        }
        return null;
    }

    private void ApplyFile(string path, string source)
    {
        Dictionary<string, object> parsed;
        try { parsed = MiniYaml.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { Console.Error.WriteLine($"warning: ignoring {path}: {ex.Message}"); return; }
        foreach (var (k, v) in parsed)
        {
            var key = Keys.Select(x => x.Key).FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (key is null) { Console.Error.WriteLine($"warning: unknown setting '{k}' in {path}"); continue; }
            if (v is List<string> list) { _lists[key] = (list, source); _values[key] = (string.Join(",", list), source); }
            else _values[key] = ((string)v, source);
        }
    }
}

/// <summary>
/// Just enough YAML for .secureflow.yml: "key: value", one level of nesting ("ingest:" then indented keys,
/// flattened to "ingest.key"), "- item" lists or [a, b] inline lists, quotes and # comments. Anything
/// fancier is a configuration smell for a file this small.
/// </summary>
public static class MiniYaml
{
    public static Dictionary<string, object> Parse(string text)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        string? listKey = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = StripComment(raw).TrimEnd();
            if (line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("- ") && listKey is not null)
            {
                ((List<string>)result[listKey]).Add(Unquote(trimmed[2..].Trim()));
                continue;
            }
            listKey = null;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) throw new FormatException($"cannot read line '{trimmed}'");
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (indent == 0) section = null;
            var fullKey = indent > 0 && section is not null ? $"{section}.{key}" : key;

            if (value.Length == 0)
            {
                if (indent == 0) section = key;
                // Could be a section header or the start of a "- item" list; decide on the next line.
                result[fullKey] = new List<string>();
                listKey = fullKey;
                continue;
            }
            if (value.StartsWith('[') && value.EndsWith(']'))
                result[fullKey] = value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).ToList();
            else
                result[fullKey] = Unquote(value);
        }
        // Section headers that never received list items are not settings.
        foreach (var k in result.Where(kv => kv.Value is List<string> { Count: 0 } && result.Keys.Any(o => o.StartsWith(kv.Key + ".", StringComparison.OrdinalIgnoreCase))).Select(kv => kv.Key).ToList())
            result.Remove(k);
        return result;
    }

    private static string StripComment(string line)
    {
        var sb = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote is null && ch == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) break;
            if (ch is '"' or '\'') quote = quote == ch ? null : quote ?? ch;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
}
