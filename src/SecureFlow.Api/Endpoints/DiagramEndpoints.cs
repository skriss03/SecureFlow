using Microsoft.AspNetCore.Mvc;
using SecureFlow.Api.Projects;
using SecureFlow.Ingest.Diagrams;

namespace SecureFlow.Api.Endpoints;

/// <summary>
/// "A diagram from any tool": one upload that accepts draw.io, Mermaid, PlantUML/C4, Structurizr, Visio (and
/// Lucidchart via .vsdx), Excalidraw and Gliffy files, which are parsed deterministically, plus images, PDFs and
/// SVG exports from any other tool, which the AI reads. The older /from-image and /from-drawio endpoints are
/// unchanged and still work.
/// </summary>
public static class DiagramEndpoints
{
    public record SampleDiagramRequest(string Sample, string? Hint);

    public static IEndpointRouteBuilder MapDiagramEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // What the upload accepts, so the UI and CLI never hard-code the list.
        api.MapGet("/diagram-formats", (DiagramImporter importer) => Results.Ok(new
        {
            maxUploadBytes = importer.Options.MaxUploadBytes,
            formats = importer.SupportedFormats().Select(f => new
            {
                format = f.Format, name = DiagramModelBuilder.DisplayName(f.Format), tools = f.Tools, extensions = f.Extensions, needsAi = f.NeedsAi,
            }),
        }));

        api.MapPost("/projects/from-diagram", async (IFormFile file, [FromForm] string? hint, ProjectPipeline pipeline, AiRegistry ai, DiagramImporter importer, CancellationToken ct) =>
        {
            if (file.Length == 0) return Results.BadRequest(new { error = "The file is empty." });
            if (file.Length > importer.Options.MaxUploadBytes)
                return Results.Problem(statusCode: 413, title: "Diagram too large", detail: $"{file.Length / 1024 / 1024} MB exceeds Diagrams:MaxUploadBytes ({importer.Options.MaxUploadBytes / 1024 / 1024} MB).");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Start(file.FileName, ms.ToArray(), hint, pipeline, ai, importer);
        }).DisableAntiforgery();

        // Sample diagrams in every supported format (samples/diagrams), so the demo needs no files to hand.
        api.MapGet("/sample-diagrams", (IHostEnvironment env, IConfiguration config, DiagramImporter importer) =>
        {
            var dir = ProjectEndpoints.SamplePath(env, config, "diagrams");
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
            var items = Directory.EnumerateFiles(dir)
                .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .Select(f =>
                {
                    var name = Path.GetFileName(f);
                    var detection = importer.Detect(name, File.ReadAllBytes(f));
                    return new
                    {
                        id = name,
                        name = $"{DiagramModelBuilder.DisplayName(detection.Format)} ({Path.GetExtension(name)})",
                        description = detection.NeedsAi ? $"{name}: read by the AI" : $"{name}: parsed exactly, no AI",
                        needsAi = detection.NeedsAi,
                        supported = detection.Route != DiagramRoute.Rejected,
                    };
                })
                .Where(x => x.supported);
            return Results.Ok(items);
        });

        api.MapPost("/projects/from-sample-diagram", (SampleDiagramRequest req, ProjectPipeline pipeline, AiRegistry ai, DiagramImporter importer, IHostEnvironment env, IConfiguration config) =>
        {
            var dir = ProjectEndpoints.SamplePath(env, config, "diagrams");
            var path = Path.GetFullPath(Path.Combine(dir, req.Sample));
            // Only files directly inside samples/diagrams; never a path that walks out of it.
            if (!path.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return Results.NotFound(new { error = "Unknown sample diagram" });
            return Start(Path.GetFileName(path), File.ReadAllBytes(path), req.Hint, pipeline, ai, importer);
        });

        return app;
    }

    private static IResult Start(string fileName, byte[] content, string? hint, ProjectPipeline pipeline, AiRegistry ai, DiagramImporter importer)
    {
        var result = importer.Import(fileName, content);
        var detection = result.Detection;
        if (detection.Route == DiagramRoute.Rejected && result.Model is null)
            return Results.Problem(statusCode: 415, title: "Diagram format not supported", detail: detection.Message ?? result.Log);

        if (result.Model is { } model)
        {
            var p = new Project { Name = model.Name, Source = detection.Format, SourceRef = fileName };
            pipeline.Start(p, (progress, _) =>
            {
                progress.Report(result.Log);
                return Task.FromResult(model);
            });
            return Results.Accepted($"/api/projects/{p.Id}", new { id = p.Id, format = detection.Format, route = "parser" });
        }

        if (!ai.Available)
        {
            var structured = string.Join(", ", importer.SupportedFormats().Where(f => !f.NeedsAi).Select(f => DiagramModelBuilder.DisplayName(f.Format)));
            return Results.Problem(statusCode: 503, title: "AI provider unavailable",
                detail: $"{result.Log} No AI provider is configured on the server ({ai.Error}). Files from {structured} work without AI.");
        }

        var project = new Project { Name = Path.GetFileNameWithoutExtension(fileName), Source = detection.Format, SourceRef = fileName };
        pipeline.Start(project, (progress, ct) =>
        {
            progress.Report(result.Log);
            return result.Detection.Route == DiagramRoute.AiImage
                ? ai.Ai!.ExtractFromImageAsync(result.AiContent!, result.AiMediaType!, hint, progress, ct)
                : ai.Ai!.ExtractFromDocumentAsync(result.AiContent!, result.AiMediaType!, detection.Format, hint, progress, ct);
        });
        return Results.Accepted($"/api/projects/{project.Id}", new { id = project.Id, format = detection.Format, route = "ai" });
    }
}
