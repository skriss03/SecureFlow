using System.Text;
using System.Text.RegularExpressions;
using SecureFlow.Core.Model;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>How a diagram file will be read.</summary>
public enum DiagramRoute
{
    /// <summary>A structured parser reads it exactly (no AI).</summary>
    Parser,
    /// <summary>The AI looks at a picture (PNG, JPEG, WebP, GIF).</summary>
    AiImage,
    /// <summary>The AI reads a PDF.</summary>
    AiPdf,
    /// <summary>The AI reads text it has no parser for (SVG markup, other diagram text).</summary>
    AiText,
    Rejected,
}

public sealed record DiagramDetection(DiagramRoute Route, string Format, IDiagramParser? Parser, string? MediaType, string? Message)
{
    public bool NeedsAi => Route is DiagramRoute.AiImage or DiagramRoute.AiPdf or DiagramRoute.AiText;
}

/// <summary>Result of the deterministic step: a model, or the AI route to use instead (with the reason).</summary>
public sealed record DiagramImportResult(ArchitectureModel? Model, DiagramDetection Detection, string Log, byte[]? AiContent = null, string? AiMediaType = null)
{
    public bool NeedsAi => Model is null && Detection.Route != DiagramRoute.Rejected;
}

/// <summary>
/// Entry point for "any diagram from any tool". Detects the format from the extension and the content, parses
/// structured formats deterministically, and otherwise says which AI route to use. Never calls the AI itself, so
/// it also runs inside the CLI for local dry runs.
/// </summary>
public sealed class DiagramImporter
{
    private readonly DiagramOptions _options;
    private readonly IReadOnlyList<IDiagramParser> _parsers;

    public DiagramImporter(DiagramOptions? options = null, IEnumerable<IDiagramParser>? parsers = null)
    {
        _options = options ?? new DiagramOptions();
        _parsers = (parsers ?? DefaultParsers()).ToList();
    }

    public static IReadOnlyList<IDiagramParser> DefaultParsers() =>
    [
        new DrawioParser(), new MermaidParser(), new PlantUmlParser(), new StructurizrParser(),
        new VisioParser(), new ExcalidrawParser(), new GliffyParser(),
    ];

    public DiagramOptions Options => _options;
    public IReadOnlyList<IDiagramParser> Parsers => _parsers.Where(p => _options.IsEnabled(p.Format)).ToList();

    /// <summary>What is accepted, for the UI's file picker and the docs.</summary>
    public IReadOnlyList<(string Format, string Tools, IReadOnlyList<string> Extensions, bool NeedsAi)> SupportedFormats()
    {
        var list = Parsers.Select(p => (p.Format, p.Tools, p.Extensions, false)).ToList();
        if (_options.IsEnabled("image")) list.Add(("image", "Any tool: PNG, JPEG, WebP or GIF export, screenshot or whiteboard photo (Figma, Miro, Lucidchart, Visio...)", [".png", ".jpg", ".jpeg", ".webp", ".gif"], true));
        if (_options.IsEnabled("pdf")) list.Add(("pdf", "Any tool: PDF export (Figma, Lucidchart, Visio, PowerPoint...)", [".pdf"], true));
        if (_options.IsEnabled("svg")) list.Add(("svg", "Any tool: SVG export (Figma, Lucidchart, Miro, Mermaid/PlantUML renders...)", [".svg"], true));
        return list;
    }

    private static readonly Dictionary<string, string> Rejections = new(StringComparer.OrdinalIgnoreCase)
    {
        [".vsd"] = "This is an older binary Visio file (.vsd). Open it in Visio and use Save As → Visio Drawing (.vsdx), or export it as PNG or PDF.",
        [".vss"] = "This is a Visio stencil, not a diagram. Upload the .vsdx drawing instead.",
        [".vssx"] = "This is a Visio stencil, not a diagram. Upload the .vsdx drawing instead.",
        [".fig"] = "Figma files (.fig) are a closed format. In Figma, select the frame and Export it as PNG, SVG or PDF, then upload that.",
        [".jam"] = "FigJam files are a closed format. Export the board as PNG or PDF, then upload that.",
        [".lucid"] = "Lucidchart files can't be read directly. In Lucidchart use File → Export → Visio (.vsdx), or PNG / PDF.",
    };

    public DiagramDetection Detect(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName);
        if (Rejections.TryGetValue(ext, out var why)) return new(DiagramRoute.Rejected, "unsupported", null, null, why);
        if (content.Length == 0) return new(DiagramRoute.Rejected, "unsupported", null, null, "The file is empty.");

        var parsers = Parsers;
        // 1. A parser whose extension matches and whose content check agrees, or whose extension is its own.
        foreach (var p in parsers.Where(p => DiagramText.HasExtension(fileName, p.Extensions)))
            if (p.Sniff(fileName, content) || HasOwnExtension(fileName, p))
                return new(DiagramRoute.Parser, p.Format, p, null, null);
        // 2. Content alone (e.g. a Mermaid diagram saved as .txt, a draw.io file renamed).
        foreach (var p in parsers)
            if (p.Sniff(fileName, content))
                return new(DiagramRoute.Parser, p.Format, p, null, null);

        // 3. AI routes.
        var image = ImageMediaType(content);
        if (image is not null)
            return _options.IsEnabled("image") ? new(DiagramRoute.AiImage, "image", null, image, null) : Disabled("image");
        if (content.Length > 4 && Encoding.ASCII.GetString(content, 0, 4) == "%PDF")
            return _options.IsEnabled("pdf") ? new(DiagramRoute.AiPdf, "pdf", null, "application/pdf", null) : Disabled("pdf");
        if (LooksLikeText(content))
        {
            var head = DiagramText.Head(content, 8192);
            if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                return _options.IsEnabled("svg") ? new(DiagramRoute.AiText, "svg", null, "image/svg+xml", null) : Disabled("svg");
            // Only text that looks like a diagram language (it has edges) goes to the AI; anything else would
            // cost an AI call to learn it isn't a diagram.
            if (_options.AiTextFallback && _options.IsEnabled("text") && LooksLikeDiagramText(DiagramText.Head(content, 64 * 1024)))
                return new(DiagramRoute.AiText, "text", null, "text/plain", null);
        }
        return new(DiagramRoute.Rejected, "unsupported", null, null,
            $"'{fileName}' is not a diagram format SecureFlow recognises. Supported: {string.Join(", ", SupportedFormats().SelectMany(f => f.Extensions).Distinct())}.");
    }

    /// <summary>
    /// Runs the deterministic step. Returns a model when a parser could read the file, or the AI route (with the
    /// prepared content) when the file needs the AI or a parser found nothing.
    /// </summary>
    public DiagramImportResult Import(string fileName, byte[] content)
    {
        if (content.LongLength > _options.MaxUploadBytes)
            return new(null, new(DiagramRoute.Rejected, "unsupported", null, null, $"The file is {content.LongLength / 1024 / 1024} MB; the limit is {_options.MaxUploadBytes / 1024 / 1024} MB (Diagrams:MaxUploadBytes)."), "");

        var detection = Detect(fileName, content);
        if (detection.Route == DiagramRoute.Rejected) return new(null, detection, detection.Message ?? "");
        if (detection.Route != DiagramRoute.Parser) return AiRoute(detection, content, $"Detected {Describe(detection)}; the AI will read it.");

        var name = DiagramModelBuilder.DisplayName(detection.Format);
        try
        {
            var graph = detection.Parser!.Parse(content, fileName);
            var model = DiagramModelBuilder.Build(graph, _options.TypeKeywords());
            if (model.Components.Count > 0)
                return new(model, detection,
                    $"Parsed {name} deterministically: {model.Components.Count} components, {model.Flows.Count} flows, {model.TrustBoundaries.Count} trust boundaries (no AI needed for this step).");
            return Fallback(detection, content, $"The {name} file had no labelled shapes the parser could use");
        }
        catch (DiagramParseException ex)
        {
            return Fallback(detection, content, $"The {name} parser could not read this file ({ex.Message})");
        }
    }

    private DiagramImportResult Fallback(DiagramDetection detection, byte[] content, string reason)
    {
        if (!_options.AiFallback)
            return new(null, new(DiagramRoute.Rejected, detection.Format, detection.Parser, null, reason + ". AI fallback is turned off (Diagrams:AiFallback)."), reason);
        var image = ImageMediaType(content);
        if (image is not null) return AiRoute(new(DiagramRoute.AiImage, detection.Format, null, image, null), content, reason + "; the AI will read the picture instead.");
        if (LooksLikeText(content)) return AiRoute(new(DiagramRoute.AiText, detection.Format, null, "text/plain", null), content, reason + "; the AI will read it instead.");
        return new(null, new(DiagramRoute.Rejected, detection.Format, detection.Parser, null, reason + ". Export it as PNG or PDF and upload that instead."), reason);
    }

    private DiagramImportResult AiRoute(DiagramDetection d, byte[] content, string log)
    {
        if (d.Route != DiagramRoute.AiText) return new(null, d, log, content, d.MediaType);
        var text = DiagramText.Decode(content);
        // SVG exports often embed fonts and bitmaps as base64; they carry no architecture and only cost tokens.
        text = Regex.Replace(text, @"data:[\w/+.-]+;base64,[A-Za-z0-9+/=\s]+", "data:...", RegexOptions.Compiled);
        if (text.Length > _options.MaxAiTextChars)
        {
            text = text[.._options.MaxAiTextChars];
            log += $" Text truncated to {_options.MaxAiTextChars} characters (Diagrams:MaxAiTextChars).";
        }
        return new(null, d, log, Encoding.UTF8.GetBytes(text), d.MediaType);
    }

    private static string Describe(DiagramDetection d) => d.Route switch
    {
        DiagramRoute.AiImage => $"an image ({d.MediaType})",
        DiagramRoute.AiPdf => "a PDF",
        DiagramRoute.AiText when d.Format == "svg" => "an SVG export",
        DiagramRoute.AiText => "a text file in a format with no dedicated parser",
        _ => d.Format,
    };

    private static DiagramDetection Disabled(string format) =>
        new(DiagramRoute.Rejected, format, null, null, $"{format} files are turned off on this server (Diagrams:DisabledFormats).");

    // Extensions that belong to one parser only; ambiguous ones (.xml, .md, .svg, .png, .json) need the content check.
    private static readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase) { ".xml", ".md", ".svg", ".png", ".json", ".txt" };
    private static bool HasOwnExtension(string fileName, IDiagramParser p) =>
        !Ambiguous.Contains(Path.GetExtension(fileName)) && DiagramText.HasExtension(fileName, p.Extensions);

    public static string? ImageMediaType(byte[] b)
    {
        if (DrawioParser.IsPng(b)) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 6 && Encoding.ASCII.GetString(b, 0, 4) == "GIF8") return "image/gif";
        if (b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return "image/webp";
        return null;
    }

    /// <summary>Diagram languages (D2, Graphviz DOT, nomnoml, ...) all describe edges with arrows or links.</summary>
    private static bool LooksLikeDiagramText(string text) =>
        Regex.IsMatch(text, @"[\w\]\)""]\s*(-+>|<-+|=+>|--|\.\.>|~>)\s*[\w\[\(""]");

    private static bool LooksLikeText(byte[] b)
    {
        var n = Math.Min(b.Length, 8192);
        for (var i = 0; i < n; i++) if (b[i] == 0) return false;
        return true;
    }
}
