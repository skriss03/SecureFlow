using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Microsoft Visio .vsdx (also what Lucidchart, Gliffy and draw.io export as "Visio"). A .vsdx is a zip of XML:
/// visio/pages/page*.xml hold shapes (text, position, master) and Connects (which connector glues to which shapes);
/// visio/masters/masters.xml names the stencil each shape came from ("Database", "User", "Server"...), which is
/// the strongest hint about what a box is. Groups with several labelled members, Visio containers and big boxes
/// drawn around other shapes become trust boundaries. Every page is read.
/// </summary>
public sealed class VisioParser : IDiagramParser
{
    public string Format => "visio";
    public string Tools => "Microsoft Visio, and Lucidchart / Gliffy / draw.io exported as Visio";
    public IReadOnlyList<string> Extensions { get; } = [".vsdx", ".vsdm"];

    public bool Sniff(string fileName, byte[] content)
    {
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K') return false;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName.StartsWith("visio/pages/", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }

    private sealed class ShapeInfo
    {
        public required string Id;
        public string Text = "";
        public string Hint = "";
        public string? ParentShape;
        public bool IsGroup;
        public bool IsConnector;
        public bool IsStructuredContainer;
        public List<string> ContainerMembers = new();
        public double? PinX, PinY, Width, Height;
        public int LinePattern;
        public int BeginArrow, EndArrow;
    }

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { throw new DiagramParseException("Not a valid .vsdx file (it is not a zip package). Older binary .vsd files must be saved as .vsdx first.", ex); }

        using (zip)
        {
            var masters = ReadMasters(zip);
            var pages = zip.Entries
                .Where(e => Regex.IsMatch(e.FullName, @"^visio/pages/page\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(e => int.Parse(Regex.Match(e.Name, @"\d+").Value))
                .ToList();
            if (pages.Count == 0) throw new DiagramParseException("This .vsdx has no pages.");

            var graph = new DiagramGraph { Format = Format, FileName = fileName };
            var title = ReadTitle(zip);
            if (!string.IsNullOrWhiteSpace(title)) graph.Name = title;

            for (var p = 0; p < pages.Count; p++)
                ReadPage(pages[p], $"p{p + 1}:", masters, graph);
            return graph;
        }
    }

    private static void ReadPage(ZipArchiveEntry entry, string prefix, Dictionary<string, string> masters, DiagramGraph graph)
    {
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);
        var root = doc.Root!;
        var shapes = new Dictionary<string, ShapeInfo>();

        void Walk(XElement shapesEl, string? parent, string inheritedMaster)
        {
            foreach (var sh in shapesEl.Elements().Where(e => e.Name.LocalName == "Shape"))
            {
                var id = prefix + (string?)sh.Attribute("ID");
                var master = (string?)sh.Attribute("Master");
                var masterName = master is not null && masters.TryGetValue(master, out var mn) ? mn : inheritedMaster;
                var nameU = (string?)sh.Attribute("NameU") ?? (string?)sh.Attribute("Name") ?? "";
                var info = new ShapeInfo
                {
                    Id = id,
                    Text = ShapeText(sh),
                    Hint = $"{masterName} {Regex.Replace(nameU, @"\.\d+$", "")}".Trim(),
                    ParentShape = parent,
                    IsGroup = (string?)sh.Attribute("Type") == "Group",
                    PinX = Cell(sh, "PinX"), PinY = Cell(sh, "PinY"), Width = Cell(sh, "Width"), Height = Cell(sh, "Height"),
                    LinePattern = (int)(Cell(sh, "LinePattern") ?? 1),
                    BeginArrow = (int)(Cell(sh, "BeginArrow") ?? 0), EndArrow = (int)(Cell(sh, "EndArrow") ?? 0),
                };
                info.IsConnector = info.Hint.Contains("connector", StringComparison.OrdinalIgnoreCase)
                    || sh.Elements().Any(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "BeginX" && ((string?)e.Attribute("F") ?? "").Contains("BegTrigger", StringComparison.Ordinal));
                // Visio 2010+ structured containers: User.msvStructureType = "Container", members in the Relationships formula.
                info.IsStructuredContainer = sh.Descendants().Any(e => e.Name.LocalName == "Row" && (string?)e.Attribute("N") == "msvStructureType"
                    && e.Descendants().Any(c => (string?)c.Attribute("V") is "Container" or "List"));
                var relationships = sh.Elements().FirstOrDefault(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "Relationships");
                if (relationships is not null)
                    info.ContainerMembers = Regex.Matches((string?)relationships.Attribute("F") ?? "", @"Sheet\.(\d+)!").Select(m => prefix + m.Groups[1].Value).ToList();
                shapes[id] = info;

                var children = sh.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
                if (children is not null) Walk(children, id, masterName);
            }
        }

        var top = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (top is null) return;
        Walk(top, null, "");

        // Connects: a connector shape glued at BeginX to one shape and at EndX to another.
        var glue = new Dictionary<string, (string? Begin, string? End)>();
        foreach (var c in root.Descendants().Where(e => e.Name.LocalName == "Connect"))
        {
            var from = prefix + (string?)c.Attribute("FromSheet");
            var to = prefix + (string?)c.Attribute("ToSheet");
            var cell = (string?)c.Attribute("FromCell") ?? "";
            glue.TryGetValue(from, out var g);
            if (cell.StartsWith("Begin", StringComparison.OrdinalIgnoreCase)) g.Begin = to;
            else if (cell.StartsWith("End", StringComparison.OrdinalIgnoreCase)) g.End = to;
            glue[from] = g;
            if (shapes.TryGetValue(from, out var conn)) conn.IsConnector = true;
        }

        // Decide what each shape is: a component, a container, or part of a bigger component (icon + caption groups).
        var representative = new Dictionary<string, string>(); // shape id -> node id it belongs to
        IEnumerable<ShapeInfo> Descendants(ShapeInfo s) =>
            shapes.Values.Where(x => x.ParentShape == s.Id).SelectMany(x => new[] { x }.Concat(Descendants(x)));

        // Document order (parents before children). Pieces of a group already merged into one component are skipped.
        foreach (var s in shapes.Values)
        {
            if (s.IsConnector || representative.ContainsKey(s.Id)) continue;
            if (s.IsGroup)
            {
                var labelled = Descendants(s).Where(d => !d.IsConnector && d.Text.Length > 0).ToList();
                if (labelled.Count >= 2 && !LooksLikeIconWithCaption(s, labelled))
                {
                    AddNode(graph, s, isContainer: true);
                    representative[s.Id] = s.Id;
                    continue; // children are added by the loop with their own ParentShape
                }
                // One logical box drawn as several pieces: it becomes one component.
                var label = s.Text.Length > 0 ? s.Text : string.Join(" ", labelled.Select(l => l.Text));
                var hint = string.Join(" ", new[] { s.Hint }.Concat(Descendants(s).Select(d => d.Hint)));
                graph.Nodes.Add(new DiagramNode { Id = s.Id, Label = label, TypeHint = hint, ParentId = ParentNode(s, representative, shapes) });
                representative[s.Id] = s.Id;
                foreach (var d in Descendants(s)) representative[d.Id] = s.Id;
                continue;
            }
            AddNode(graph, s, s.IsStructuredContainer);
            representative[s.Id] = s.Id;
        }
        // Parents: group membership first, then Visio container relationships.
        foreach (var n in graph.Nodes.Where(n => n.Id.StartsWith(prefix, StringComparison.Ordinal)))
            if (shapes.TryGetValue(n.Id, out var s)) n.ParentId ??= ParentNode(s, representative, shapes);
        foreach (var container in shapes.Values.Where(s => s.IsStructuredContainer))
            foreach (var member in container.ContainerMembers)
                if (representative.TryGetValue(member, out var rep) && rep != container.Id && graph.Find(rep) is { } mn && mn.ParentId is null)
                    mn.ParentId = container.Id;

        // Boxes drawn around other boxes (typical of Lucidchart exports) become containers too.
        var topLevel = graph.Nodes
            .Where(n => n.Id.StartsWith(prefix, StringComparison.Ordinal) && n.ParentId is null && shapes.TryGetValue(n.Id, out var si) && si.Width > 0)
            .Select(n => (n, BoxOf(shapes[n.Id])))
            .ToList();
        Containment.Assign(topLevel);

        foreach (var (connId, g) in glue)
        {
            if (g.Begin is null || g.End is null || !shapes.TryGetValue(connId, out var conn)) continue;
            if (!representative.TryGetValue(g.Begin, out var a) || !representative.TryGetValue(g.End, out var b) || a == b) continue;
            var reverse = conn.BeginArrow > 0 && conn.EndArrow == 0;
            graph.Edges.Add(new DiagramEdge
            {
                SourceId = reverse ? b : a, TargetId = reverse ? a : b, Label = conn.Text,
                Dashed = conn.LinePattern > 1, Style = conn.Hint,
            });
        }
    }

    private static bool LooksLikeIconWithCaption(ShapeInfo group, List<ShapeInfo> labelled) =>
        labelled.Count == 2 && labelled.Any(l => l.Text.Length <= 3); // e.g. an icon with "DB" plus its caption

    private static string? ParentNode(ShapeInfo s, Dictionary<string, string> representative, Dictionary<string, ShapeInfo> shapes)
    {
        var p = s.ParentShape;
        while (p is not null)
        {
            if (representative.TryGetValue(p, out var rep) && rep == p) return p;
            p = shapes.TryGetValue(p, out var ps) ? ps.ParentShape : null;
        }
        return null;
    }

    private static void AddNode(DiagramGraph graph, ShapeInfo s, bool isContainer) =>
        graph.Nodes.Add(new DiagramNode { Id = s.Id, Label = s.Text, TypeHint = s.Hint + (s.LinePattern > 1 ? " dashed" : ""), IsContainer = isContainer });

    private static Box BoxOf(ShapeInfo s) => Box.FromCenter(s.PinX ?? 0, s.PinY ?? 0, s.Width ?? 0, s.Height ?? 0);

    private static string ShapeText(XElement shape)
    {
        var text = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
        return text is null ? "" : DiagramModelBuilder.StripHtml(string.Concat(text.DescendantNodes().OfType<XText>().Select(t => t.Value + " ")));
    }

    private static double? Cell(XElement shape, string name)
    {
        var cell = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
        return cell is not null && double.TryParse((string?)cell.Attribute("V"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static Dictionary<string, string> ReadMasters(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var entry = zip.GetEntry("visio/masters/masters.xml");
        if (entry is null) return map;
        using var s = entry.Open();
        foreach (var m in XDocument.Load(s).Descendants().Where(e => e.Name.LocalName == "Master"))
            if ((string?)m.Attribute("ID") is { } id)
                map[id] = (string?)m.Attribute("NameU") ?? (string?)m.Attribute("Name") ?? "";
        return map;
    }

    private static string? ReadTitle(ZipArchive zip)
    {
        var entry = zip.GetEntry("docProps/core.xml");
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s).Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
    }
}
