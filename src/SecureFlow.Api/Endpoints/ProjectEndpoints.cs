using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SecureFlow.Ai;
using SecureFlow.Api.Options;
using SecureFlow.Api.Projects;
using SecureFlow.Api.Reports;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Samples;
using SecureFlow.Core.Simulation;
using SecureFlow.Ingest;

namespace SecureFlow.Api.Endpoints;

public static class ProjectEndpoints
{
    public record RepoRequest(string Url, string? Branch);
    public record SampleRequest(string Sample);
    public record SimulateRequest(string ComponentId);
    public record StatusRequest(FindingStatus Status);
    public record ApplyFixRequest(string FindingId);
    public record GroupRequest(string Name);
    public record AssignGroupRequest(string? GroupId);
    public record SampleImageRequest(string Sample, string? Hint);

    /// <summary>Upload from the CLI / build plugins: a digest built (and redacted) on the developer's machine.</summary>
    public record ScanRequest(string? ProjectKey, string? Name, string? Group, string? Branch, string? Commit, string? Source, RepoDigest Digest);

    /// <summary>Architecture images shipped in samples/, so the vision path can be demoed without a file to hand.</summary>
    private static readonly Dictionary<string, (string Name, string Description, string File, string MediaType)> SampleImages = new()
    {
        ["shopfast-diagram"] = ("ShopFast diagram", "A clean architecture diagram of the flawed ShopFast platform, read by the vision model.", "diagram.png", "image/png"),
        ["payments-platform"] = ("NorthBank Payments", "A retail banking transfer and settlement platform: mostly sound, with a single-instance fraud service and an unauthenticated internal hop.", "payments-platform.png", "image/png"),
    };

    internal static string SamplePath(IHostEnvironment env, IConfiguration config, string file)
    {
        var dir = config["Storage:SamplesDir"] ?? Path.Combine("..", "..", "samples");
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(env.ContentRootPath, dir);
        return Path.GetFullPath(Path.Combine(dir, file));
    }

    private static readonly Dictionary<string, (string Name, string Description, Func<ArchitectureModel> Build)> Samples = new()
    {
        ["shopfast"] = ("ShopFast (flawed e-commerce)", "Single SQL instance shared by three services, sync chain with no timeouts, secrets in appsettings, worker called synchronously.", SampleModels.ShopFast),
        ["healthy"] = ("Healthy reference", "A small, well-built system: redundant, queued, authenticated, encrypted.", SampleModels.Healthy),
    };

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (ProjectStore store, AiRegistry ai, SecureFlowOptions options, DependencyScanner deps, HttpRequest request) => Results.Ok(new
        {
            status = "ok",
            ai = new { provider = ai.Name, available = ai.Available, error = ai.Error },
            projects = store.All.Count(),
            offline = Environment.GetEnvironmentVariable("SECUREFLOW_AI_OFFLINE") is "1" or "true",
            baseUrl = options.BaseUrl(request),
            authRequired = options.Auth.Enabled,
            retentionDays = options.Retention.Enabled ? options.Retention.Days : (int?)null,
            dependencyScan = deps.Enabled && options.Scan.AutoDependencyScan,
        }));

        api.MapGet("/rules", () => Results.Ok(RuleCatalog.Describe()));

        api.MapGet("/samples", () => Results.Ok(Samples.Select(kv => new { id = kv.Key, name = kv.Value.Name, description = kv.Value.Description })));

        // Only advertise sample images whose file is actually present, so the UI hides them otherwise.
        api.MapGet("/sample-images", (IHostEnvironment env, IConfiguration config) => Results.Ok(
            SampleImages.Where(kv => File.Exists(SamplePath(env, config, kv.Value.File)))
                        .Select(kv => new { id = kv.Key, name = kv.Value.Name, description = kv.Value.Description })));

        api.MapPost("/projects/from-sample-image", async (SampleImageRequest req, ProjectPipeline pipeline, AiRegistry ai, IHostEnvironment env, IConfiguration config) =>
        {
            if (!ai.Available) return Results.Problem(statusCode: 503, title: "AI provider unavailable", detail: ai.Error);
            if (!SampleImages.TryGetValue(req.Sample, out var s)) return Results.NotFound(new { error = "Unknown sample image" });
            var path = SamplePath(env, config, s.File);
            if (!File.Exists(path)) return Results.NotFound(new { error = $"Sample image is missing: {s.File}" });

            var bytes = await File.ReadAllBytesAsync(path);
            var p = new Project { Name = s.Name, Source = "image", SourceRef = s.File };
            pipeline.Start(p, (progress, ct) => ai.Ai!.ExtractFromImageAsync(bytes, s.MediaType, req.Hint, progress, ct));
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        });

        // groupIds: comma-separated filter used by the Group Manager dashboard. Absent = every project.
        api.MapGet("/projects", (string? groupIds, ProjectStore store, GroupStore groups) =>
        {
            var wanted = groupIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var items = store.All;
            if (wanted is { Count: > 0 })
                items = items.Where(p => p.GroupId is not null && wanted.Contains(p.GroupId));
            return Results.Ok(items.Select(p => ProjectSummary.From(p, groups)));
        });

        // Creates any missing demo-portfolio project and scans it for real, in the background.
        api.MapPost("/demo/seed", (DemoSeeder seeder) =>
        {
            var created = seeder.Seed();
            return Results.Ok(new { created = created.Count, ids = created.Select(p => p.Id), running = seeder.IsRunning });
        });

        // ----- Groups -----
        api.MapGet("/groups", (GroupStore groups) => Results.Ok(groups.All));

        api.MapPost("/groups", (GroupRequest req, GroupStore groups) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.BadRequest(new { error = "name is required" })
                : Results.Ok(groups.Add(req.Name.Trim())));

        api.MapDelete("/groups/{id}", (string id, GroupStore groups, ProjectStore store) =>
        {
            if (!groups.Delete(id)) return Results.NotFound();
            foreach (var p in store.All.Where(p => p.GroupId == id).ToList())
            {
                p.GroupId = null;
                store.Save(p);
            }
            return Results.NoContent();
        });

        api.MapPost("/projects/{id}/group", (string id, AssignGroupRequest req, ProjectStore store, GroupStore groups) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            if (req.GroupId is not null && groups.Get(req.GroupId) is null) return Results.BadRequest(new { error = "Unknown group" });
            p.GroupId = req.GroupId;
            store.Save(p);
            return Results.Ok(ProjectSummary.From(p, groups));
        });

        api.MapPost("/projects/from-sample", (SampleRequest req, ProjectPipeline pipeline) =>
        {
            if (!Samples.TryGetValue(req.Sample.ToLowerInvariant(), out var s)) return Results.NotFound(new { error = "Unknown sample" });
            var p = new Project { Name = s.Name, Source = "sample", SourceRef = req.Sample };
            pipeline.Start(p, (_, _) => Task.FromResult(s.Build()));
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        });

        api.MapPost("/projects/from-json", (ArchitectureModel model, ProjectPipeline pipeline) =>
        {
            var p = new Project { Name = model.Name, Source = "json" };
            pipeline.Start(p, (_, _) => Task.FromResult(model));
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        });

        api.MapPost("/projects/from-image", async (IFormFile file, [FromForm] string? hint, ProjectPipeline pipeline, AiRegistry ai) =>
        {
            if (!ai.Available) return Results.Problem(statusCode: 503, title: "AI provider unavailable", detail: ai.Error);
            if (file.Length == 0 || file.Length > 20 * 1024 * 1024) return Results.BadRequest(new { error = "Image must be between 1 byte and 20 MB." });
            var mediaType = file.ContentType?.ToLowerInvariant() switch
            {
                "image/png" => "image/png",
                "image/jpeg" or "image/jpg" => "image/jpeg",
                "image/webp" => "image/webp",
                "image/gif" => "image/gif",
                _ => Path.GetExtension(file.FileName).ToLowerInvariant() switch
                {
                    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
                    _ => null,
                },
            };
            if (mediaType is null) return Results.BadRequest(new { error = "Unsupported image type. Use PNG, JPEG, WebP or GIF." });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var p = new Project { Name = Path.GetFileNameWithoutExtension(file.FileName), Source = "image", SourceRef = file.FileName };
            pipeline.Start(p, (progress, ct) => ai.Ai!.ExtractFromImageAsync(bytes, mediaType, hint, progress, ct));
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        }).DisableAntiforgery();

        api.MapPost("/projects/from-drawio", async (IFormFile file, ProjectPipeline pipeline) =>
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var xml = await reader.ReadToEndAsync();
            ArchitectureModel model;
            try { model = DrawioIngest.Parse(xml, file.FileName); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Could not parse draw.io file: " + ex.Message }); }
            var p = new Project { Name = model.Name, Source = "drawio", SourceRef = file.FileName };
            pipeline.Start(p, (progress, _) =>
            {
                progress.Report($"Parsed draw.io deterministically: {model.Components.Count} shapes, {model.Flows.Count} edges (no AI needed for this step).");
                return Task.FromResult(model);
            });
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        }).DisableAntiforgery();

        // A "root" URL (https://github.com/<owner>, no repo segment) fans out into one project per
        // repository under that org/user instead of a single project; a specific repo URL behaves as before.
        api.MapPost("/projects/from-repo", async (RepoRequest req, ProjectPipeline pipeline, AiRegistry ai, RepoIngest ingest, OrgScanner orgScanner, SecureFlowOptions options, CancellationToken ct) =>
        {
            if (!ai.Available) return Results.Problem(statusCode: 503, title: "AI provider unavailable", detail: ai.Error);
            if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "url is required" });

            if (GitHostDiscovery.TryGetOwner(req.Url, out var owner))
            {
                try
                {
                    var projects = await orgScanner.StartAsync(owner, ct);
                    return Results.Accepted($"/api/projects", new { ids = projects.Select(p => p.Id), count = projects.Count, owner });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            var name = req.Url.TrimEnd('/').Split('/').Last().Replace(".git", "");
            var p = new Project { Name = name, Source = "repo", SourceRef = req.Url };
            pipeline.Start(p, async (progress, ct) =>
            {
                var digest = await ingest.BuildAsync(req.Url, req.Branch, progress, ct);
                if (options.Scan.RedactServerSideScans) ingest.Redact(digest, progress);
                p.Digest = digest;
                p.Branch = digest.Branch; p.Commit = digest.Commit;
                return await ai.Ai!.ExtractFromRepoDigestAsync(digest, progress, ct);
            });
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id });
        });

        // ----- Developer integration: CLI and build plugins upload a locally built digest -----
        // Same pipeline as a repo scan; the only difference is where the digest was built. With a
        // projectKey, repeat scans update one project so its report URL stays stable.
        api.MapPost("/scans", async (HttpRequest http, ProjectPipeline pipeline, ProjectStore store, GroupStore groups, AiRegistry ai, SecureFlowOptions options) =>
        {
            if (!ai.Available && !options.Scan.AllowWithoutAi) return Results.Problem(statusCode: 503, title: "AI provider unavailable", detail: ai.Error);
            if (http.ContentLength is { } len && len > options.Upload.MaxDigestBytes)
                return Results.Problem(statusCode: 413, title: "Digest too large", detail: $"{len} bytes exceeds SecureFlow:Upload:MaxDigestBytes ({options.Upload.MaxDigestBytes}). Lower Ingest:BudgetChars on the client or raise the server limit.");

            ScanRequest? req;
            try { req = await http.ReadFromJsonAsync<ScanRequest>(ScanJson); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "Invalid scan payload: " + ex.Message }); }
            if (req?.Digest is null || (req.Digest.Files.Count == 0 && req.Digest.Dependencies.Count == 0))
                return Results.BadRequest(new { error = "A digest with at least one file is required." });

            string? groupId = null;
            if (!string.IsNullOrWhiteSpace(req.Group))
            {
                var g = groups.Resolve(req.Group, options.Scan.AutoCreateGroups);
                if (g is null) return Results.BadRequest(new { error = $"Unknown group '{req.Group}' (SecureFlow:Scan:AutoCreateGroups is off)." });
                groupId = g.Id;
            }

            var key = string.IsNullOrWhiteSpace(req.ProjectKey) ? null : req.ProjectKey.Trim();
            var name = string.IsNullOrWhiteSpace(req.Name) ? (key?.Split('@')[0] ?? "uploaded-repo") : req.Name.Trim();
            var digest = req.Digest;
            ProjectPipeline.Extractor extract = async (progress, ct) =>
            {
                progress.Report($"Received digest from {req.Source ?? "client"}: {digest.Files.Count} files, {digest.Dependencies.Count} dependencies, {digest.IncludedBytes / 1000}k characters.");
                if (!ai.Available)
                {
                    // Deterministic-only scan: no architecture model, but the dependency CVE scan still runs.
                    progress.Report($"Architecture extraction skipped: no AI provider is configured on the server ({ai.Error}). Dependency vulnerabilities are still checked.");
                    return new ArchitectureModel { Name = name };
                }
                return await ai.Ai!.ExtractFromRepoDigestAsync(digest, progress, ct);
            };

            var existing = options.Scan.ReuseProjectByKey && key is not null ? store.FindByKey(key) : null;
            if (existing is not null && existing.Status is not (ProjectStatus.Ready or ProjectStatus.Error))
                return Results.Conflict(new { error = $"A scan of '{key}' is already running.", id = existing.Id, url = options.ReportUrl(http, existing.Id) });

            Project p;
            if (existing is not null)
            {
                p = existing;
                p.Digest = digest;
                p.Branch = digest.Branch ?? req.Branch;
                p.Commit = digest.Commit ?? req.Commit;
                p.SourceRef = digest.RepoUrl;
                if (groupId is not null) p.GroupId = groupId;
                pipeline.Rescan(p, extract);
            }
            else
            {
                p = new Project
                {
                    Name = name, Source = "repo", SourceRef = digest.RepoUrl, Key = key, GroupId = groupId,
                    Branch = digest.Branch ?? req.Branch, Commit = digest.Commit ?? req.Commit, Digest = digest,
                };
                pipeline.Start(p, extract);
            }
            return Results.Accepted($"/api/projects/{p.Id}/summary", ScanResponse(p, http, options));
        });

        // Compact status for clients polling a scan; the full project document is large.
        api.MapGet("/projects/{id}/summary", (string id, ProjectStore store, HttpRequest http, SecureFlowOptions options) =>
            store.Get(id) is { } p ? Results.Ok(ScanResponse(p, http, options)) : Results.NotFound());

        api.MapGet("/projects/{id}", (string id, ProjectStore store) =>
            store.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());

        api.MapDelete("/projects/{id}", (string id, ProjectStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/projects/{id}/model.json", (string id, ProjectStore store) =>
            store.Get(id) is { } p
                ? Results.Text(JsonSerializer.Serialize(p.Model, new JsonSerializerOptions(AiJson.Options) { WriteIndented = true }), "application/json")
                : Results.NotFound());

        // Server-Sent Events: log lines and status changes until the pipeline reaches a terminal state.
        api.MapGet("/projects/{id}/events", async (string id, HttpContext ctx, ProjectStore store, CancellationToken ct) =>
        {
            var p = store.Get(id);
            if (p is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var sent = 0;
            ProjectStatus? lastStatus = null;
            var idle = 0;
            while (!ct.IsCancellationRequested)
            {
                var entries = p.LogSince(sent);
                foreach (var e in entries)
                    await ctx.Response.WriteAsync($"event: log\ndata: {JsonSerializer.Serialize(e, AiJson.Options)}\n\n", ct);
                sent += entries.Count;
                if (p.Status != lastStatus)
                {
                    lastStatus = p.Status;
                    await ctx.Response.WriteAsync($"event: status\ndata: {JsonSerializer.Serialize(new { status = p.Status, p.Error }, AiJson.Options)}\n\n", ct);
                }
                await ctx.Response.Body.FlushAsync(ct);
                if (p.Status is ProjectStatus.Ready or ProjectStatus.Error)
                {
                    // Stay open briefly so a fix in progress can stream too, then close.
                    if (++idle > 8) break;
                }
                await Task.Delay(400, ct);
            }
        });

        api.MapPost("/projects/{id}/scan-vulnerabilities", async (string id, ProjectStore store, ProjectPipeline pipeline, CancellationToken ct) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            if (p.Digest is null) return Results.BadRequest(new { error = "No dependency manifest available. Vulnerability scanning needs a repo scan (image/draw.io/sample projects have no package manifests to check)." });
            try { await pipeline.ScanDependenciesAsync(p, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                p.AddLog("Vulnerability scan failed: " + ex.Message, "error");
                store.Save(p);
                return Results.Problem(statusCode: 502, title: "OSV lookup failed", detail: ex.Message);
            }
            return Results.Ok(p);
        });

        api.MapPost("/projects/{id}/analyze", async (string id, ProjectStore store, ProjectPipeline pipeline, CancellationToken ct) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            pipeline.Analyze(p, "Re-analysis");
            await pipeline.ReviewAsync(p, null, ct);
            p.Status = ProjectStatus.Ready;
            store.Save(p);
            return Results.Ok(p);
        });

        api.MapPost("/projects/{id}/simulate", (string id, SimulateRequest req, ProjectStore store) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            try { return Results.Ok(BlastRadius.Simulate(p.Model, req.ComponentId)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapGet("/projects/{id}/simulate/{componentId}", (string id, string componentId, ProjectStore store) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            try { return Results.Ok(BlastRadius.Simulate(p.Model, componentId)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/projects/{id}/findings/{fid}/fix", async (string id, string fid, ProjectStore store, ProjectPipeline pipeline, CancellationToken ct) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            try { return Results.Ok(await pipeline.ProposeFixAsync(p, fid, ct)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (AiUnavailableException ex) { return Results.Problem(statusCode: 503, title: "AI provider unavailable", detail: ex.Message); }
            catch (AiResponseException ex) { return Results.Problem(statusCode: 502, title: "AI response could not be used", detail: ex.Message); }
        });

        api.MapPost("/projects/{id}/findings/{fid}/status", (string id, string fid, StatusRequest req, ProjectStore store, ProjectPipeline pipeline) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            var f = p.Findings.FirstOrDefault(x => x.Id == fid);
            if (f is null) return Results.NotFound();
            f.Status = req.Status;
            p.Score = Core.Scoring.Scorer.Score(p.Findings);
            p.Snapshot($"Marked \"{f.Title}\" as {req.Status}");
            store.Save(p);
            return Results.Ok(p);
        });

        api.MapPost("/projects/{id}/apply-fix", (string id, ApplyFixRequest req, ProjectStore store, ProjectPipeline pipeline) =>
        {
            var p = store.Get(id);
            if (p is null) return Results.NotFound();
            try { return Results.Ok(pipeline.ApplyFix(p, req.FindingId)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = "Patch could not be applied: " + ex.Message }); }
        });

        api.MapGet("/projects/{id}/report", (string id, ProjectStore store) =>
            store.Get(id) is { } p ? Results.Text(ReportBuilder.Markdown(p), "text/markdown; charset=utf-8") : Results.NotFound());

        return app;
    }

    private static readonly JsonSerializerOptions ScanJson = new(AiJson.Options) { PropertyNameCaseInsensitive = true };

    private static object ScanResponse(Project p, HttpRequest http, SecureFlowOptions options)
    {
        var baseUrl = options.BaseUrl(http);
        return new
        {
            id = p.Id,
            key = p.Key,
            name = p.Name,
            status = p.Status,
            error = p.Error,
            scanCount = p.ScanCount,
            branch = p.Branch,
            commit = p.Commit,
            url = options.ReportUrl(http, p.Id),
            reportMarkdownUrl = $"{baseUrl}/api/projects/{p.Id}/report",
            statusUrl = $"{baseUrl}/api/projects/{p.Id}/summary",
            score = new { p.Score.Resilience, p.Score.ResilienceGrade, p.Score.Security, p.Score.SecurityGrade },
            openBySeverity = p.Score.OpenBySeverity.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            openFindings = p.Findings.Count(f => f.Status == FindingStatus.Open),
            openVulnerabilities = p.Findings.Count(f => f.Status == FindingStatus.Open && f.Category == FindingCategory.Vulnerability),
            aiReview = p.Analysis is not null,
            // False when no architecture model could be built (e.g. no AI provider): scores then reflect CVEs only.
            architectureAnalysed = p.Model.Components.Count > 0,
            lastLog = p.Log.LastOrDefault()?.Message,
        };
    }
}
