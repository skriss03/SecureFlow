using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// draw.io / diagrams.net: .drawio and .xml files, plus the "editable" exports that embed the diagram XML:
/// .drawio.svg (in the svg's content attribute) and .drawio.png (in a PNG text chunk named "mxfile").
/// </summary>
public sealed class DrawioParser : IDiagramParser
{
    public string Format => "drawio";
    public string Tools => "draw.io / diagrams.net";
    public IReadOnlyList<string> Extensions { get; } = [".drawio", ".drawio.xml", ".drawio.svg", ".drawio.png", ".xml"];

    public bool Sniff(string fileName, byte[] content)
    {
        if (IsPng(content)) return PngText(content, "mxfile") is not null;
        var head = DiagramText.Head(content, 64 * 1024);
        return head.Contains("<mxfile", StringComparison.Ordinal) || head.Contains("<mxGraphModel", StringComparison.Ordinal)
            || head.Contains("&lt;mxfile", StringComparison.Ordinal) || head.Contains("&lt;mxGraphModel", StringComparison.Ordinal);
    }

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        string xml;
        if (IsPng(content))
        {
            xml = PngText(content, "mxfile") ?? throw new DiagramParseException("This PNG has no embedded draw.io diagram. Export it from draw.io with 'Include a copy of my diagram'.");
            if (!xml.TrimStart().StartsWith('<')) xml = Uri.UnescapeDataString(xml);
        }
        else
        {
            xml = DiagramText.Decode(content);
            if (xml.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !xml.TrimStart().StartsWith("<mxfile", StringComparison.Ordinal))
            {
                // Editable SVG export: <svg content="&lt;mxfile ...&gt;">. XDocument decodes the attribute for us.
                var svg = Load(xml);
                xml = (string?)svg.Root?.Attribute("content")
                    ?? throw new DiagramParseException("This SVG has no embedded draw.io diagram (no 'content' attribute).");
            }
        }
        return ParseXml(xml, fileName);
    }

    public DiagramGraph ParseXml(string xmlText, string? fileName)
    {
        var doc = Load(xmlText);
        var model = doc.Descendants("mxGraphModel").FirstOrDefault();
        if (model is null)
        {
            // Compressed diagram: <diagram> text is base64(deflate(urlencode(xml)))
            var diagram = doc.Descendants("diagram").FirstOrDefault()
                ?? throw new DiagramParseException("Not a draw.io file: no <mxGraphModel> or <diagram> element.");
            var inner = Decompress(diagram.Value.Trim());
            model = Load(inner).Descendants("mxGraphModel").FirstOrDefault()
                ?? throw new DiagramParseException("Could not decode the draw.io diagram.");
        }

        var graph = new DiagramGraph { Format = Format, FileName = fileName };
        var cells = model.Descendants("mxCell").ToList();

        // Labels may live on a wrapping <object label="..."><mxCell .../></object>
        string LabelOf(XElement cell)
        {
            var v = (string?)cell.Attribute("value");
            if (string.IsNullOrWhiteSpace(v) && cell.Parent?.Name == "object")
                v = (string?)cell.Parent.Attribute("label");
            return DiagramModelBuilder.StripHtml(v ?? "");
        }
        string Id(XElement c) => (string?)c.Attribute("id") ?? (string?)c.Parent?.Attribute("id") ?? "";

        var vertices = cells.Where(c => (string?)c.Attribute("vertex") == "1").ToList();
        var edges = cells.Where(c => (string?)c.Attribute("edge") == "1").ToList();
        var edgeIds = edges.Select(Id).ToHashSet();

        // Containers = vertices that have other vertices as children (or swimlane/group styles).
        var childCount = vertices.GroupBy(v => (string?)v.Attribute("parent") ?? "").ToDictionary(g => g.Key, g => g.Count());
        bool IsContainer(XElement v)
        {
            var style = ((string?)v.Attribute("style") ?? "").ToLowerInvariant();
            return childCount.ContainsKey(Id(v)) || style.Contains("swimlane") || style.Contains("group") || style.Contains("container=1");
        }

        foreach (var v in vertices)
        {
            var parent = (string?)v.Attribute("parent");
            if (parent is not null && edgeIds.Contains(parent)) continue; // edge label, not a shape
            graph.Nodes.Add(new DiagramNode
            {
                Id = Id(v),
                Label = LabelOf(v),
                TypeHint = (string?)v.Attribute("style") ?? "",
                ParentId = parent,
                IsContainer = IsContainer(v),
            });
        }

        foreach (var e in edges)
        {
            var src = (string?)e.Attribute("source"); var tgt = (string?)e.Attribute("target");
            if (src is null || tgt is null) continue;
            var eid = Id(e);
            // edge labels are often child cells whose parent is the edge
            var childLabels = vertices.Where(v => (string?)v.Attribute("parent") == eid).Select(LabelOf).Where(s => s.Length > 0);
            var label = string.Join(" ", new[] { LabelOf(e) }.Concat(childLabels)).Trim();
            var style = (string?)e.Attribute("style") ?? "";
            graph.Edges.Add(new DiagramEdge
            {
                SourceId = src, TargetId = tgt, Label = label, Style = style,
                Dashed = style.Contains("dashed=1", StringComparison.OrdinalIgnoreCase),
            });
        }
        return graph;
    }

    private static XDocument Load(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex) { throw new DiagramParseException("Not valid draw.io XML: " + ex.Message, ex); }
    }

    private static string Decompress(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var input = new MemoryStream(bytes);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return Uri.UnescapeDataString(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new DiagramParseException("Could not decode the compressed draw.io diagram.", ex);
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static bool IsPng(byte[] b) => b.Length > 8 && b.AsSpan(0, 8).SequenceEqual(PngSignature);

    /// <summary>Value of a tEXt, zTXt or iTXt chunk with the given keyword, or null.</summary>
    internal static string? PngText(byte[] png, string keyword)
    {
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0 || pos + 12 + len > png.Length) break;
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var nul = data.IndexOf((byte)0);
                if (nul > 0 && Encoding.Latin1.GetString(data[..nul]) == keyword)
                {
                    var rest = data[(nul + 1)..];
                    if (type == "tEXt") return Encoding.Latin1.GetString(rest);
                    if (type == "zTXt") return Inflate(rest[1..].ToArray(), Encoding.Latin1);
                    // iTXt: compression flag, method, language\0, translated keyword\0, text
                    var compressed = rest[0] == 1;
                    var afterLang = rest[2..]; afterLang = afterLang[(afterLang.IndexOf((byte)0) + 1)..];
                    var text = afterLang[(afterLang.IndexOf((byte)0) + 1)..];
                    return compressed ? Inflate(text.ToArray(), Encoding.UTF8) : Encoding.UTF8.GetString(text);
                }
            }
            if (type == "IEND") break;
            pos += 12 + len;
        }
        return null;
    }

    private static string Inflate(byte[] zlib, Encoding enc)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(z, enc);
        return reader.ReadToEnd();
    }
}
