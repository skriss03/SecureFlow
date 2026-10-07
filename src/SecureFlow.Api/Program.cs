using System.Text.Json.Serialization;
using SecureFlow.Ai;
using SecureFlow.Ai.Caching;
using SecureFlow.Ai.Providers;
using SecureFlow.Api.Auth;
using SecureFlow.Api.Endpoints;
using SecureFlow.Api.Options;
using SecureFlow.Api.Projects;
using SecureFlow.Ingest;

var builder = WebApplication.CreateBuilder(args);

// User Secrets (dotnet user-secrets) is only auto-loaded by the framework in the Development
// environment. This is a local dev/demo tool with no launchSettings.json forcing that environment,
// so load it explicitly here -- it is a no-op (empty) if no secrets have been set.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// ----- AI provider (optional: the deterministic engine works without one) -----
var aiOptions = builder.Configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
if (!Path.IsPathRooted(aiOptions.CacheDir))
    aiOptions.CacheDir = Path.Combine(builder.Environment.ContentRootPath, aiOptions.CacheDir);
builder.Services.AddSingleton(aiOptions);
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        IArchitectureAi provider = aiOptions.Provider.ToLowerInvariant() switch
        {
            "openai" => new OpenAiArchitectureAi(aiOptions),
            "anthropic" => new AnthropicArchitectureAi(aiOptions),
            "foundry" => new FoundryArchitectureAi(aiOptions),
            var other => throw new AiUnavailableException($"Unknown Ai:Provider '{other}'. Use 'anthropic', 'openai', or 'foundry'."),
        };
        if (aiOptions.AnthropicKeyHint(aiOptions.Provider) is { } keyHint)
            log.LogWarning("AI key check: {Hint} Scans will fail with an authentication error until this is fixed.", keyHint);
        var cache = new ReplayCache(aiOptions.CacheDir, aiOptions.CacheEnabled, aiOptions.ResolvedOffline);
        log.LogInformation("AI provider: {Name}. Replay cache: {Dir} (offline-only: {Offline})", provider.Name, aiOptions.CacheDir, aiOptions.ResolvedOffline);
        return new AiRegistry { Ai = new CachingArchitectureAi(provider, cache) };
    }
    catch (AiUnavailableException ex)
    {
        log.LogWarning("AI provider unavailable: {Message}. Rules, scoring and blast radius still work.", ex.Message);
        return new AiRegistry { Error = ex.Message };
    }
});

// ----- Configuration (every value overridable by env var, e.g. SecureFlow__Auth__Enabled=true) -----
var sfOptions = builder.Configuration.GetSection(SecureFlowOptions.Section).Get<SecureFlowOptions>() ?? new SecureFlowOptions();
builder.Services.AddSingleton(sfOptions);
var ingestOptions = builder.Configuration.GetSection(RepoIngestOptions.Section).Get<RepoIngestOptions>() ?? new RepoIngestOptions();
var depOptions = builder.Configuration.GetSection(DependencyScanOptions.Section).Get<DependencyScanOptions>() ?? new DependencyScanOptions();
builder.Services.AddSingleton(depOptions);

// ----- Core services -----
var dataDir = builder.Configuration["Storage:DataDir"] ?? "data";
if (!Path.IsPathRooted(dataDir)) dataDir = Path.Combine(builder.Environment.ContentRootPath, dataDir);
if (string.IsNullOrWhiteSpace(ingestOptions.WorkDir)) ingestOptions.WorkDir = Path.Combine(dataDir, "tmp");
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<GroupStore>();
builder.Services.AddSingleton<ProjectPipeline>();
builder.Services.AddSingleton<DemoSeeder>();
builder.Services.AddHttpClient<GitHostDiscovery>();
builder.Services.AddSingleton<OrgScanner>();
builder.Services.AddHttpClient<DependencyScanner>();
builder.Services.AddSingleton(new RepoIngest(ingestOptions));
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 25 * 1024 * 1024);
// Kestrel's default body cap is 30 MB; raise it if a larger digest upload is configured.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Math.Max(30L * 1024 * 1024, sfOptions.Upload.MaxDigestBytes));

var app = builder.Build();

app.UseCors();
app.UseMiddleware<TokenAuthMiddleware>();
app.MapProjectEndpoints();

if (sfOptions.Auth.Enabled && sfOptions.Auth.Tokens.All(string.IsNullOrWhiteSpace))
    app.Logger.LogWarning("SecureFlow:Auth:Enabled is true but no tokens are configured; every write request will be rejected.");
app.Logger.LogInformation("Report links use {Base}. Auth {Auth}. Retention {Retention}.",
    sfOptions.PublicBaseUrl ?? "the request host", sfOptions.Auth.Enabled ? "on" : "off",
    sfOptions.Retention.Enabled ? $"{sfOptions.Retention.Days} days" : "off");

// Serve the built SPA when present (web/ → dotnet publish copies to wwwroot). Vite dev server is used otherwise.
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(wwwroot, "index.html")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}
else
{
    app.MapGet("/", () => Results.Text("SecureFlow API is running. Start the web UI with `npm run dev` in ./web, or build it into wwwroot.", "text/plain"));
}

// Warm the AI registry so a bad key is reported at startup rather than on first use.
_ = app.Services.GetRequiredService<AiRegistry>();

app.Run();
