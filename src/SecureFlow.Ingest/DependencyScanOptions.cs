namespace SecureFlow.Ingest;

/// <summary>CVE scanning settings, bound from the "DependencyScan" configuration section.</summary>
public sealed class DependencyScanOptions
{
    public const string Section = "DependencyScan";

    public bool Enabled { get; set; } = true;
    /// <summary>OSV-compatible API root. Point it at an internal mirror or proxy when the server cannot reach osv.dev.</summary>
    public string OsvBaseUrl { get; set; } = "https://api.osv.dev/";
    /// <summary>Hard cap so a pathological monorepo can't blow up one batch query.</summary>
    public int MaxPackages { get; set; } = 1000;
    /// <summary>OSV accepts at most 1000 queries per batch call.</summary>
    public int BatchSize { get; set; } = 500;
    /// <summary>
    /// Ecosystems to check (OSV names: npm, NuGet, PyPI, Go, Maven). Empty = all supported. Defaults to empty
    /// rather than the full list because the configuration binder appends to a pre-filled array.
    /// </summary>
    public string[] Ecosystems { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Severity used when an advisory publishes none: Low, Medium, High or Critical.</summary>
    public string UnratedSeverity { get; set; } = "Medium";
}
