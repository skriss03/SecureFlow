using System.Text.Json;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Excalidraw (.excalidraw JSON, also what Obsidian and VS Code's Excalidraw extensions save). Rectangles, ellipses
/// and diamonds are shapes, labelled by their bound text (or a loose text placed inside them); arrows bound to two
/// shapes are flows, labelled by their bound text; frames, and boxes drawn around other shapes, are boundaries.
/// </summary>
public sealed class ExcalidrawParser : IDiagramParser
{
    public string Format => "excalidraw";
    public string Tools => "Excalidraw (incl. Obsidian / VS Code Excalidraw)";
    public IReadOnlyList<string> Extensions { get; } = [".excalidraw", ".excalidraw.json"];

    public bool Sniff(string fileName, byte[] content)
    {
        var head = DiagramText.Head(content, 2048);
        return head.Contains("\"type\"", StringComparison.Ordinal) && head.Contains("\"excalidraw\"", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> ShapeTypes = ["rectangle", "ellipse", "diamond", "frame", "magicframe"];

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new DiagramParseException("Not valid Excalidraw JSON: " + ex.Message, ex); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                throw new DiagramParseException("Not an Excalidraw file: no \"elements\" array.");

            var graph = new DiagramGraph { Format = Format, FileName = fileName };
            var els = elements.EnumerateArray().Where(e => !(e.TryGetProperty("isDeleted", out var d) && d.ValueKind == JsonValueKind.True)).ToList();
            string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            double Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string? Bound(JsonElement e, string p) => e.TryGetProperty(p, out var b) && b.ValueKind == JsonValueKind.Object ? Str(b, "elementId") : null;

            var texts = els.Where(e => Str(e, "type") == "text").ToList();
            var boundText = texts.Where(t => Str(t, "containerId").Length > 0)
                .GroupBy(t => Str(t, "containerId"))
                .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(t => Str(t, "text"))));

            var shapes = new List<(DiagramNode Node, Box Box)>();
            foreach (var e in els.Where(e => ShapeTypes.Contains(Str(e, "type"))))
            {
                var id = Str(e, "id");
                var isFrame = Str(e, "type").EndsWith("frame", StringComparison.Ordinal);
                var label = isFrame ? Str(e, "name") : boundText.GetValueOrDefault(id, "");
                var dashed = Str(e, "strokeStyle") is "dashed" or "dotted" ? " dashed" : "";
                var node = new DiagramNode { Id = id, Label = Clean(label), TypeHint = Str(e, "type") + dashed, IsContainer = isFrame };
                graph.Nodes.Add(node);
                shapes.Add((node, new Box(Num(e, "x"), Num(e, "y"), Num(e, "width"), Num(e, "height"))));
            }

            // Frames hold their children explicitly.
            foreach (var e in els.Where(e => Str(e, "frameId").Length > 0))
                if (graph.Find(Str(e, "id")) is { } child && child.Id != Str(e, "frameId")) child.ParentId = Str(e, "frameId");

            // Loose text (not bound to anything) labels the smallest shape it sits in; a zone's title is usually
            // a loose text in its top corner.
            foreach (var t in texts.Where(t => Str(t, "containerId").Length == 0))
            {
                var cx = Num(t, "x") + Num(t, "width") / 2; var cy = Num(t, "y") + Num(t, "height") / 2;
                var host = shapes.Where(s => s.Box.ContainsPoint(cx, cy)).OrderBy(s => s.Box.Area).FirstOrDefault();
                if (host.Node is not null && host.Node.Label.Length == 0) host.Node.Label = Clean(Str(t, "text"));
            }

            Containment.Assign(shapes.Where(s => !s.Node.IsContainer && s.Node.ParentId is null).ToList());
            // A box drawn around others only gets its title text as a label; a zone with no title stays unnamed.

            foreach (var a in els.Where(e => Str(e, "type") is "arrow" or "line"))
            {
                var start = Bound(a, "startBinding"); var end = Bound(a, "endBinding");
                if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end)) continue;
                var startHead = Str(a, "startArrowhead"); var endHead = Str(a, "endArrowhead");
                var reverse = startHead.Length > 0 && endHead.Length == 0 && a.TryGetProperty("endArrowhead", out var eh) && eh.ValueKind == JsonValueKind.Null;
                graph.Edges.Add(new DiagramEdge
                {
                    SourceId = reverse ? end : start, TargetId = reverse ? start : end,
                    Label = Clean(boundText.GetValueOrDefault(Str(a, "id"), "")),
                    Dashed = Str(a, "strokeStyle") is "dashed" or "dotted",
                });
            }
            return graph;
        }
    }

    private static string Clean(string s) => DiagramModelBuilder.StripHtml(s.Replace("\n", " "));
}
