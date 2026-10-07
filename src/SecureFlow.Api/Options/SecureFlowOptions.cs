namespace SecureFlow.Api.Options;

/// <summary>
/// Server behaviour that changes between a laptop and a shared deployment (OCP). Bound from the
/// "SecureFlow" configuration section; every value can be overridden by an environment variable,
/// e.g. SecureFlow__Auth__Enabled=true, SecureFlow__Auth__Tokens__0=..., SecureFlow__Retention__Days=30.
/// </summary>
public sealed class SecureFlowOptions
{
    public const string Section = "SecureFlow";

    /// <summary>
    /// Base URL used when the server hands out report links (e.g. https://secureflow.apps.ocp.example.com).
    /// Null = derived from the incoming request, which is right for localhost but wrong behind a proxy
    /// that rewrites the host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Path of the report page in the web UI; {id} is replaced with the project id.</summary>
    public string ReportPathTemplate { get; set; } = "/p/{id}";

    public AuthOptions Auth { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public UploadOptions Upload { get; set; } = new();
    public ScanOptions Scan { get; set; } = new();

    public sealed class AuthOptions
    {
        /// <summary>When true, every non-GET /api request needs "Authorization: Bearer &lt;token&gt;".</summary>
        public bool Enabled { get; set; }
        /// <summary>Accepted tokens. Supply them through a secret (env var or mounted file), never appsettings.json.</summary>
        public string[] Tokens { get; set; } = [];
        /// <summary>Also require a token for GET /api requests (reports, project lists).</summary>
        public bool ProtectReads { get; set; }
        public string HeaderName { get; set; } = "Authorization";
        public string Scheme { get; set; } = "Bearer";
    }

    public sealed class RetentionOptions
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Projects not updated for this many days are deleted.</summary>
        public int Days { get; set; } = 30;
        public int SweepIntervalMinutes { get; set; } = 60;
        /// <summary>Delay before the first sweep after startup.</summary>
        public int InitialDelaySeconds { get; set; } = 30;
    }

    public sealed class UploadOptions
    {
        /// <summary>Largest accepted /api/scans request body.</summary>
        public long MaxDigestBytes { get; set; } = 10 * 1024 * 1024;
    }

    public sealed class ScanOptions
    {
        /// <summary>Run the CVE scan automatically whenever a project has a repo digest.</summary>
        public bool AutoDependencyScan { get; set; } = true;
        /// <summary>A scan with an existing project key updates that project (keeps history, statuses and group).</summary>
        public bool ReuseProjectByKey { get; set; } = true;
        /// <summary>Redact secrets in digests built on the server (URL or server-local path scans). CLI uploads are redacted client-side.</summary>
        public bool RedactServerSideScans { get; set; }
        /// <summary>Create a group when a scan names one that doesn't exist yet.</summary>
        public bool AutoCreateGroups { get; set; } = true;
        /// <summary>
        /// Accept uploads when no AI provider is configured: the CVE scan still runs and the report says
        /// that architecture extraction is unavailable. False = reject with 503 like the UI repo scan.
        /// </summary>
        public bool AllowWithoutAi { get; set; } = true;
    }

    /// <summary>Report page URL for a project, from <see cref="PublicBaseUrl"/> or the request.</summary>
    public string ReportUrl(HttpRequest request, string projectId) =>
        BaseUrl(request) + ReportPathTemplate.Replace("{id}", projectId);

    public string BaseUrl(HttpRequest request) =>
        (string.IsNullOrWhiteSpace(PublicBaseUrl) ? $"{request.Scheme}://{request.Host}{request.PathBase}" : PublicBaseUrl).TrimEnd('/');
}
