using System.Text.RegularExpressions;
using SecureFlow.Ai;

namespace SecureFlow.Ingest;

/// <summary>
/// Replaces literal secret values in a digest before it leaves the machine it was built on. The key and
/// the fact that a literal value was present are kept, so the hard-coded-secret findings still fire with
/// evidence; only the value itself is removed. References to the environment or a vault
/// (${X}, #{x}, %X%, {{x}}, process.env, os.environ...) are left alone because they are not secrets.
/// </summary>
public sealed class SecretRedactor
{
    private readonly RedactionOptions _options;
    private readonly Regex _jsonLike;
    private readonly Regex _assignment;
    private readonly Regex _quotedAssignment;
    private readonly Regex _k8sEnv;

    private static readonly Regex InlinePassword = new(@"(?i)\b(password|pwd|passwd|secret|accountkey|sharedaccesskey)\s*=\s*([^;""'\s]+)", RegexOptions.Compiled);
    private static readonly Regex UrlCredentials = new(@"(?i)\b([a-z][a-z0-9+.\-]*://[^:/\s""'@]+:)([^@/\s""']+)(@)", RegexOptions.Compiled);
    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled);
    private static readonly Regex Pem = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex CodeFile = new(@"\.(cs|java|kt|kts|groovy|scala|py|ts|tsx|js|jsx|mjs|cjs|go|rb|php)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SecretRedactor(RedactionOptions options)
    {
        _options = options;
        var key = $"[\\w.\\-]*(?:{options.KeyPattern})[\\w.\\-]*";
        // "Key": "value"  /  'KEY': 'value'  (JSON, Python dicts, JS objects)
        _jsonLike = new Regex($@"(?i)([""']{key}[""']\s*:\s*)([""'])((?:(?!\2).)*)(\2)", RegexOptions.Compiled);
        // key: value  /  key=value  /  KEY = value  (YAML, .properties, .env, ini, toml) — unquoted or quoted
        _assignment = new Regex($@"(?im)^(\s*(?:-\s*)?(?:(?:export|ENV|ARG|set)\s+)?{key}\s*[:=]\s*)(.+?)\s*$", RegexOptions.Compiled);
        // Kubernetes / compose env lists:  - name: DB_PASSWORD \n   value: s3cret
        _k8sEnv = new Regex($@"(?im)^(\s*-?\s*name:\s*[""']?{key}[""']?\s*\r?\n\s*value:\s*)(.+?)\s*$", RegexOptions.Compiled);
        // SECRET_KEY = "value" in code: only quoted literals, never expressions.
        _quotedAssignment = new Regex($@"(?i)\b({key}\s*[:=]\s*)([""'])((?:(?!\2).){{4,}})(\2)", RegexOptions.Compiled);
    }

    /// <summary>Redacts every file in place and returns how many values were replaced.</summary>
    public int Redact(RepoDigest digest)
    {
        if (!_options.Enabled) return 0;
        var total = 0;
        foreach (var f in digest.Files)
        {
            var (text, count) = Redact(f.Path, f.Content);
            if (count > 0) { f.Content = text; total += count; }
        }
        return total;
    }

    public (string Text, int Count) Redact(string path, string content)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(content)) return (content, 0);
        var count = 0;
        var ph = _options.Placeholder;
        string Count(string replacement) { count++; return replacement; }

        content = Pem.Replace(content, _ => Count(ph));
        content = Jwt.Replace(content, _ => Count(ph));
        content = UrlCredentials.Replace(content, m => IsReference(m.Groups[2].Value) ? m.Value : Count(m.Groups[1].Value + ph + m.Groups[3].Value));
        content = InlinePassword.Replace(content, m => IsReference(m.Groups[2].Value) || m.Groups[2].Value == ph ? m.Value : Count($"{m.Groups[1].Value}={ph}"));
        content = _jsonLike.Replace(content, m => IsReference(m.Groups[3].Value) || m.Groups[3].Value.Contains(ph) ? m.Value : Count(m.Groups[1].Value + m.Groups[2].Value + ph + m.Groups[4].Value));

        if (CodeFile.IsMatch(path))
        {
            content = _quotedAssignment.Replace(content, m => IsReference(m.Groups[3].Value) || m.Groups[3].Value.Contains(ph) ? m.Value : Count(m.Groups[1].Value + m.Groups[2].Value + ph + m.Groups[4].Value));
        }
        else
        {
            content = _k8sEnv.Replace(content, m => IsReference(m.Groups[2].Value) || m.Groups[2].Value.Contains(ph) ? m.Value : Count(m.Groups[1].Value + ph));
            content = _assignment.Replace(content, m =>
            {
                var value = m.Groups[2].Value;
                var bare = value.Trim().Trim('"', '\'');
                // Nested YAML maps ("db:" alone), JSON already handled, references and redacted values stay.
                if (bare.Length == 0 || bare.StartsWith('{') || bare.StartsWith('[') || IsReference(bare) || bare.Contains(ph)) return m.Value;
                return Count(m.Groups[1].Value + ph);
            });
        }
        return (content, count);
    }

    private static bool IsReference(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        return v.Length == 0
            || v.StartsWith("${") || v.StartsWith("#{") || v.StartsWith("{{") || v.StartsWith("$(") || v.StartsWith('$')
            || (v.StartsWith('%') && v.EndsWith('%'))
            || v.StartsWith("env:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("vault:", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase)
            || v.Contains("process.env") || v.Contains("os.environ") || v.Contains("getenv", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Environment.GetEnvironmentVariable");
    }
}
