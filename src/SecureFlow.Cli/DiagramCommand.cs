using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Scoring;
using SecureFlow.Ingest.Diagrams;

namespace SecureFlow.Cli;

/// <summary>
/// `secureflow diagram &lt;file&gt;`: analyse an architecture diagram from any tool. The file is uploaded to the
/// SecureFlow server, which parses structured formats (draw.io, Mermaid, PlantUML/C4, Structurizr, Visio,
/// Excalidraw, Gliffy) exactly and reads images, PDFs and SVG exports with the AI. With --dry-run, structured
/// formats are parsed and checked against the rules locally, with no upload and no AI.
/// </summary>
public static class DiagramCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var file = Opt(args, "--file") ?? args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(args, a));
        if (string.IsNullOrEmpty(file)) { Console.Error.WriteLine("usage: secureflow diagram <file> [--hint \"...\"] [--open] [--dry-run] [--fail-on high]"); return 1; }
        if (!File.Exists(file)) { Console.Error.WriteLine($"error: file not found: {file}"); return 1; }
        var path = Path.GetFullPath(file);
        var cfg = ClientConfig.Load(args, Path.GetDirectoryName(path)!);
        var jsonOut = args.Contains("--json");
        var log = jsonOut ? Console.Error : Console.Out;
        var bytes = await File.ReadAllBytesAsync(path);
        var name = Path.GetFileName(path);

        var importer = new DiagramImporter();
        var local = importer.Import(name, bytes);
        log.WriteLine($"SecureFlow {ScanCommand.Version} - diagram {path}");
        if (local.Detection.Route == DiagramRoute.Rejected && local.Model is null)
        {
            Console.Error.WriteLine("error: " + (local.Detection.Message ?? local.Log));
            return 1;
        }
        log.WriteLine("  " + local.Log);

        if (args.Contains("--dry-run"))
        {
            if (local.Model is null)
            {
                log.WriteLine("  Dry run: this file needs the AI, which only runs on the server. Nothing was sent.");
                return 0;
            }
            PrintLocal(local.Model, log);
            return 0;
        }

        using var http = ScanCommand.CreateClient(cfg);
        log.WriteLine($"  Uploading to {cfg.Server}...");
        HttpResponseMessage res;
        try
        {
            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", name);
            if (Opt(args, "--hint") is { } hint) form.Add(new StringContent(hint), "hint");
            res = await http.PostAsync("api/projects/from-diagram", form);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"error: cannot reach SecureFlow at {cfg.Server}: {ex.Message}");
            return 1;
        }
        if (!res.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error: server returned {(int)res.StatusCode} {res.ReasonPhrase}: {await res.Content.ReadAsStringAsync()}");
            if (res.StatusCode == HttpStatusCode.Unauthorized) Console.Error.WriteLine("       Set --token or SECUREFLOW_TOKEN.");
            if (res.StatusCode == HttpStatusCode.NotFound) Console.Error.WriteLine("       This server is older and has no diagram upload; update it, or export the diagram as PNG and use the web UI.");
            return 1;
        }
        var accepted = await res.Content.ReadFromJsonAsync<Accepted>();
        if (accepted?.Id is null) { Console.Error.WriteLine("error: empty response from server."); return 1; }

        var summary = await http.GetFromJsonAsync<ScanCommand.ScanSummary>($"api/projects/{accepted.Id}/summary");
        if (summary is null) { Console.Error.WriteLine("error: could not read the project status."); return 1; }
        return await ScanCommand.FollowAsync(http, cfg, summary, log, jsonOut,
            accepted.Route == "ai"
                ? "Server is analysing: the AI reads the diagram, then resilience + STRIDE rules"
                : "Server is analysing: resilience + STRIDE rules on the parsed diagram");
    }

    private sealed record Accepted(string? Id, string? Format, string? Route);

    private static void PrintLocal(Core.Model.ArchitectureModel model, TextWriter log)
    {
        log.WriteLine();
        log.WriteLine($"{model.Name}: {model.Components.Count} components, {model.Flows.Count} flows, {model.TrustBoundaries.Count} trust boundaries");
        foreach (var c in model.Components)
            log.WriteLine($"  [{c.Type,-13}] {c.Name}{(c.Props.Replicas is { } r ? $"  x{r}" : "")}{(c.Props.Technology is { } t ? $"  ({t})" : "")}");
        foreach (var f in model.Flows)
            log.WriteLine($"  {f.From} -> {f.To}  {f.Protocol ?? "?"}{(f.Auth is { } a ? " auth=" + a : "")}{(f.IsSync == false ? " async" : "")}");
        foreach (var b in model.TrustBoundaries)
            log.WriteLine($"  boundary {b.Name}: {string.Join(", ", b.ComponentIds)}");

        var findings = RuleCatalog.Evaluate(model);
        var score = Scorer.Score(findings);
        log.WriteLine();
        log.WriteLine($"Rules (local, no AI): {findings.Count} findings. Resilience {score.Resilience} ({score.ResilienceGrade}), Security {score.Security} ({score.SecurityGrade}).");
        foreach (var f in findings.OrderByDescending(f => f.Severity).Take(10))
            log.WriteLine($"  {f.Severity,-8} {f.RuleId} {f.Title}");
        if (findings.Count > 10) log.WriteLine($"  ... {findings.Count - 10} more. Run without --dry-run for the full report.");
    }

    private static string? Opt(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    private static readonly string[] ValueOptions = ["--hint", "--server", "--token", "--group", "--name", "--fail-on", "--out", "--timeout-seconds", "--project-key", "--file"];
    private static bool IsOptionValue(IReadOnlyList<string> args, string value)
    {
        for (var i = 1; i < args.Count; i++) if (args[i] == value && ValueOptions.Contains(args[i - 1])) return true;
        return false;
    }
}
