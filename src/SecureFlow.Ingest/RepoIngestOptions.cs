namespace SecureFlow.Ingest;

/// <summary>
/// Everything that decides what goes into a repo digest. Bound from the "Ingest" configuration section on
/// the server and from the "ingest" block of .secureflow.yml in the CLI. Defaults reproduce the original
/// hard-coded behaviour; list settings left null use the built-in defaults, and the Extra* lists append.
/// </summary>
public sealed class RepoIngestOptions
{
    public const string Section = "Ingest";

    /// <summary>Where remote repositories are cloned. Null = a "secureflow" folder under the system temp directory.</summary>
    public string? WorkDir { get; set; }

    /// <summary>Total characters of file content included (roughly 4 chars per token).</summary>
    public int BudgetChars { get; set; } = 480_000;
    public int PerFileCapChars { get; set; } = 24_000;
    public int MaxFiles { get; set; } = 140;
    /// <summary>How many source files with wiring signals may be added after the priority groups.</summary>
    public int MaxWiringFiles { get; set; } = 40;
    /// <summary>Source files larger than this are never read for wiring signals.</summary>
    public int MaxSourceFileBytes { get; set; } = 200_000;
    /// <summary>Manifests and lockfiles larger than this are skipped when resolving dependencies.</summary>
    public int MaxManifestBytes { get; set; } = 20_000_000;
    /// <summary>Directory tree lines sent to the model before truncating.</summary>
    public int MaxTreeLines { get; set; } = 350;

    /// <summary>Directory names never descended into. Null = <see cref="Defaults.SkipDirs"/>.</summary>
    public string[]? SkipDirs { get; set; }
    /// <summary>File extensions never read. Null = <see cref="Defaults.BinaryExtensions"/>.</summary>
    public string[]? BinaryExtensions { get; set; }

    /// <summary>Additional path regexes treated as configuration (included right after the built-in priority groups).</summary>
    public string[] ExtraPriorityPatterns { get; set; } = [];
    /// <summary>Additional source-code regexes that mark a file as describing service wiring.</summary>
    public string[] ExtraWiringSignals { get; set; } = [];
    /// <summary>Additional directory names to skip, on top of <see cref="SkipDirs"/>.</summary>
    public string[] ExtraSkipDirs { get; set; } = [];

    /// <summary>Resolve dependencies from manifests and lockfiles into the digest, for CVE scanning.</summary>
    public bool ResolveDependencies { get; set; } = true;

    public RedactionOptions Redaction { get; set; } = new();

    public IReadOnlyList<string> EffectiveSkipDirs => (SkipDirs ?? Defaults.SkipDirs).Concat(ExtraSkipDirs).ToArray();
    public IReadOnlyList<string> EffectiveBinaryExtensions => BinaryExtensions ?? Defaults.BinaryExtensions;

    public static class Defaults
    {
        public static readonly string[] SkipDirs =
        {
            ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".idea", "packages", "vendor", "target",
            "__pycache__", ".venv", "venv", ".terraform", "coverage", "TestResults", ".next", "out", ".gradle",
            ".angular", ".mvn", ".pytest_cache", ".mypy_cache", ".tox",
        };

        public static readonly string[] BinaryExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".pdf", ".zip", ".gz", ".tar", ".dll", ".exe", ".pdb",
            ".jar", ".class", ".so", ".dylib", ".woff", ".woff2", ".ttf", ".mp4", ".mp3", ".snap", ".pyc", ".war",
        };
    }
}

/// <summary>Secret redaction applied to configuration files before a digest leaves the machine it was built on.</summary>
public sealed class RedactionOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Keys whose values are replaced. Matched case-insensitively against the key name.</summary>
    public string KeyPattern { get; set; } = "password|passwd|pwd|secret|token|apikey|api_key|api-key|accesskey|access_key|privatekey|private_key|connectionstring|connection_string|credential|clientsecret|client_secret";
    public string Placeholder { get; set; } = "***REDACTED***";
}
