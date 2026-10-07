using System.Text.Json;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Gliffy (.gliffy JSON, the format behind Gliffy for Confluence and Jira). stage.objects holds shapes and lines;
/// a shape's label is a Text child (HTML), a line's ends point at shapes through startConstraint / endConstraint
/// nodeIds. The stencil uid ("...database...", "...cloud...") is the type hint. Groups and swimlanes, and boxes
/// drawn around other shapes, become boundaries. Built from the published JSON structure; any file it can't read
/// still gets analysed through the AI route.
/// </summary>
public sealed class GliffyParser : IDiagramParser
{
    public string Format => "gliffy";
    public string Tools => "Gliffy (Confluence, Jira)";
    public IReadOnlyList<string> Extensions { get; } = [".gliffy"];

    public bool Sniff(string fileName, byte[] content)
    {
        var head = DiagramText.Head(content, 4096);
        return head.Contains("\"stage\"", StringComparison.Ordinal) && (head.Contains("gliffy", StringComparison.OrdinalIgnoreCase) || head.Contains("\"objects\"", StringComparison.Ordinal));
    }

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new DiagramParseException("Not valid Gliffy JSON: " + ex.Message, ex); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("stage", out var stage) || !stage.TryGetProperty("objects", out var objects))
                throw new DiagramParseException("Not a Gliffy file: no stage.objects.");

            var graph = new DiagramGraph { Format = Format, FileName = fileName };
            if (doc.RootElement.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString()!.Trim().Length > 0)
                graph.Name = title.GetString()!.Trim();
            var shapes = new List<(DiagramNode Node, Box Box)>();
            var lines = new List<JsonElement>();
            Walk(objects, null, 0, 0, graph, shapes, lines);

            Containment.Assign(shapes.Where(s => !s.Node.IsContainer && s.Node.ParentId is null).ToList());

            foreach (var line in lines)
            {
                var start = ConstraintNode(line, "startConstraint", "StartPositionConstraint");
                var end = ConstraintNode(line, "endConstraint", "EndPositionConstraint");
                if (start is null || end is null) continue;
                var dash = line.TryGetProperty("graphic", out var g) && g.TryGetProperty("Line", out var l) && l.TryGetProperty("dashStyle", out var ds) && ds.ValueKind == JsonValueKind.String && ds.GetString()!.Length > 0;
                graph.Edges.Add(new DiagramEdge { SourceId = start, TargetId = end, Label = TextOf(line), Dashed = dash });
            }
            return graph;
        }
    }

    private static void Walk(JsonElement objects, string? parent, double offX, double offY, DiagramGraph graph, List<(DiagramNode, Box)> shapes, List<JsonElement> lines)
    {
        foreach (var o in objects.EnumerateArray())
        {
            var type = o.TryGetProperty("graphic", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var uid = o.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            var id = o.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString("N");
            double Num(string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var x = offX + Num("x"); var y = offY + Num("y");

            if (type == "Line") { lines.Add(o); continue; }
            if (type == "Text") continue; // labels are read through their owner

            var isGroup = uid.Contains("group", StringComparison.OrdinalIgnoreCase) || uid.Contains("swimlane", StringComparison.OrdinalIgnoreCase);
            if (type is "Shape" or "Svg" or "Image" || isGroup)
            {
                var dashed = g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Shape", out var shp) && shp.TryGetProperty("dashStyle", out var dst)
                    && dst.ValueKind == JsonValueKind.String && dst.GetString()!.Length > 0 ? " dashed" : "";
                var node = new DiagramNode
                {
                    Id = id, Label = TextOf(o), TypeHint = uid + " " + (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Shape", out var sh) && sh.TryGetProperty("tid", out var tid) ? tid.GetString() : "") + dashed,
                    ParentId = parent, IsContainer = isGroup,
                };
                graph.Nodes.Add(node);
                if (parent is null) shapes.Add((node, new Box(x, y, Num("width"), Num("height"))));
                if (isGroup && o.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                    Walk(kids, id, x, y, graph, shapes, lines);
            }
        }
    }

    private static string? ConstraintNode(JsonElement line, string which, string kind)
    {
        if (!line.TryGetProperty("constraints", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (!c.TryGetProperty(which, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty(kind, out var k) || k.ValueKind != JsonValueKind.Object) return null;
        return k.TryGetProperty("nodeId", out var n) ? n.ToString() : null;
    }

    /// <summary>Text of the first Text child (Gliffy stores labels as HTML in graphic.Text.html).</summary>
    private static string TextOf(JsonElement o)
    {
        if (!o.TryGetProperty("children", out var kids) || kids.ValueKind != JsonValueKind.Array) return "";
        foreach (var k in kids.EnumerateArray())
            if (k.TryGetProperty("graphic", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Text", out var t)
                && t.TryGetProperty("html", out var h) && h.ValueKind == JsonValueKind.String)
                return DiagramModelBuilder.StripHtml(h.GetString()!);
        return "";
    }
}
