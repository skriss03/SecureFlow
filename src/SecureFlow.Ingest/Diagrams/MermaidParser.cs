using System.Text.RegularExpressions;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Mermaid: flowchart / graph, architecture-beta and the C4 diagram types, from .mmd / .mermaid files or
/// ```mermaid blocks inside Markdown (every block in the file is read and merged).
/// </summary>
public sealed class MermaidParser : IDiagramParser
{
    public string Format => "mermaid";
    public string Tools => "Mermaid (GitHub, GitLab, Confluence, Notion, Azure DevOps wikis)";
    public IReadOnlyList<string> Extensions { get; } = [".mmd", ".mermaid", ".md"];

    private static readonly Regex Header = new(@"^\s*(flowchart|graph|architecture-beta|C4Context|C4Container|C4Component|C4Dynamic|C4Deployment)\b", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Fence = new(@"^```\s*mermaid\s*$(?<body>[\s\S]*?)^```", RegexOptions.Compiled | RegexOptions.Multiline);

    public bool Sniff(string fileName, byte[] content)
    {
        var text = DiagramText.Head(content, 64 * 1024);
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Fence.IsMatch(text);
        return Header.IsMatch(StripFrontMatter(text));
    }

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        var text = DiagramText.Decode(content).Replace("\r\n", "\n");
        var graph = new DiagramGraph { Format = Format, FileName = fileName, HasLineNumbers = true };

        // Blocks: the whole file, or each ```mermaid fence with its starting line so evidence lines stay true.
        var blocks = new List<(string Body, int FirstLine)>();
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || Fence.IsMatch(text))
        {
            foreach (Match f in Fence.Matches(text))
                blocks.Add((f.Groups["body"].Value, text[..f.Groups["body"].Index].Count(c => c == '\n') + 1));
        }
        else blocks.Add((text, 1));
        if (blocks.Count == 0) throw new DiagramParseException("No Mermaid diagram found.");

        var parsedAny = false;
        graph.Name ??= blocks.Select(b => FrontMatterTitle(b.Body)).FirstOrDefault(t => t is not null);
        foreach (var (body, firstLine) in blocks)
        {
            var lines = body.Split('\n');
            var headerIndex = Array.FindIndex(lines, l => Header.IsMatch(l));
            if (headerIndex < 0) { graph.Notes.Add("A Mermaid block that is not a flowchart, architecture or C4 diagram was skipped."); continue; }
            var type = Header.Match(lines[headerIndex]).Groups[1].Value;
            parsedAny = true;
            if (type is "flowchart" or "graph") ParseFlowchart(lines, headerIndex, firstLine, graph);
            else if (type == "architecture-beta") ParseArchitecture(lines, headerIndex, firstLine, graph);
            else ParseC4(lines, headerIndex, firstLine, graph);
        }
        if (!parsedAny) throw new DiagramParseException("No Mermaid flowchart, architecture or C4 diagram found (sequence, class and other diagram types describe behaviour, not deployment).");
        return graph;
    }

    // ---------- flowchart / graph ----------

    private static readonly Regex Ignored = new(@"^(classDef|class|style|linkStyle|click|direction|accTitle|accDescr)\b", RegexOptions.Compiled);
    private static readonly Regex NodeId = new(@"\G\s*(?<id>[A-Za-z0-9_][\w\-.]*)", RegexOptions.Compiled);
    // Arrow operators: -->, --->, ---, -.->, -.-, ==>, ===, --o, --x, <-->, ~~~ ; optional |label| after the operator.
    private static readonly Regex Arrow = new(@"\G\s*(?<op><?(?:-{2,}>|-{3,}|-\.+->|-\.+-|={2,}>|={3,}|-{2,}[ox]|~{3,}))\s*(?:\|(?<label>[^|]*)\|)?", RegexOptions.Compiled);
    // Label in the middle of the arrow: -- text -->, -. text .->, == text ==>
    private static readonly Regex InlineArrow = new(@"\G\s*(?<lead><?(?:--|-\.|==))\s+(?<label>[^-=.>|][^>|]*?)\s+(?<op>-{2,}>|-{3,}|\.+->|\.+-|={2,}>|={3,})", RegexOptions.Compiled);

    private static void ParseFlowchart(string[] lines, int headerIndex, int firstLine, DiagramGraph graph)
    {
        var parents = new Stack<string>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var lineNo = firstLine + i;
            foreach (var raw in lines[i].Split(';'))
            {
                var line = StripComment(raw).Trim();
                if (line.Length == 0 || Ignored.IsMatch(line)) continue;

                if (line.StartsWith("subgraph", StringComparison.Ordinal))
                {
                    var rest = line["subgraph".Length..].Trim();
                    var m = Regex.Match(rest, @"^(?<id>[\w\-.]+)\s*\[(?<label>[^\]]*)\]\s*$");
                    string id, label;
                    if (m.Success) { id = m.Groups["id"].Value; label = Unquote(m.Groups["label"].Value); }
                    else { label = Unquote(rest); id = Regex.IsMatch(label, @"^[\w\-.]+$") ? label : "subgraph_" + DiagramModelBuilder.Slug(label); }
                    var node = graph.Upsert(id, label, "subgraph", lineNo);
                    node.IsContainer = true;
                    node.ParentId ??= parents.Count > 0 ? parents.Peek() : null;
                    parents.Push(id);
                    continue;
                }
                if (line == "end") { if (parents.Count > 0) parents.Pop(); continue; }

                ParseStatement(line, lineNo, graph, parents.Count > 0 ? parents.Peek() : null);
            }
        }
    }

    /// <summary>One statement: "A[Label] --> B & C -->|x| D". Node groups joined with &amp; fan out.</summary>
    private static void ParseStatement(string line, int lineNo, DiagramGraph graph, string? parent)
    {
        var pos = 0;
        var previous = ReadNodeGroup(line, ref pos, lineNo, graph, parent);
        if (previous.Count == 0) return;
        while (pos < line.Length)
        {
            string label; string op; bool reverse;
            var im = InlineArrow.Match(line, pos);
            var am = Arrow.Match(line, pos);
            if (im.Success)
            {
                label = im.Groups["label"].Value; op = im.Groups["lead"].Value + im.Groups["op"].Value;
                pos = im.Index + im.Length;
            }
            else if (am.Success)
            {
                label = am.Groups["label"].Value; op = am.Groups["op"].Value;
                pos = am.Index + am.Length;
            }
            else return;
            reverse = op.StartsWith('<') && !op.EndsWith('>');
            var next = ReadNodeGroup(line, ref pos, lineNo, graph, parent);
            if (next.Count == 0) return;
            if (!op.StartsWith('~'))
                foreach (var a in previous)
                    foreach (var b in next)
                        graph.Edges.Add(new DiagramEdge
                        {
                            SourceId = reverse ? b : a, TargetId = reverse ? a : b, Label = Unquote(label.Trim()),
                            Dashed = op.Contains('.'), Line = lineNo,
                        });
            previous = next;
        }
    }

    private static List<string> ReadNodeGroup(string line, ref int pos, int lineNo, DiagramGraph graph, string? parent)
    {
        var ids = new List<string>();
        while (true)
        {
            var id = ReadNode(line, ref pos, lineNo, graph, parent);
            if (id is null) break;
            ids.Add(id);
            var amp = Regex.Match(line[pos..], @"^\s*&");
            if (!amp.Success) break;
            pos += amp.Length;
        }
        return ids;
    }

    // Shapes, longest opener first. The type hint carries the shape's usual meaning (cylinder = database).
    private static readonly (string Open, string Close, string Hint)[] Shapes =
    [
        ("(((", ")))", "circle"), ("((", "))", "circle"), ("([", "])", "stadium"), ("[[", "]]", "subroutine"),
        ("[(", ")]", "cylinder database"), ("{{", "}}", "hexagon"), ("[/", "/]", "parallelogram"), ("[\\", "\\]", "parallelogram"),
        ("[/", "\\]", "trapezoid"), ("[\\", "/]", "trapezoid"), ("(", ")", "rounded"), ("[", "]", "box"), ("{", "}", "decision"), (">", "]", "flag"),
    ];

    private static string? ReadNode(string line, ref int pos, int lineNo, DiagramGraph graph, string? parent)
    {
        var m = NodeId.Match(line, pos);
        if (!m.Success) return null;
        var id = m.Groups["id"].Value;
        if (id is "end" or "subgraph") return null;
        pos = m.Index + m.Length;
        string? label = null, hint = null;

        // v11 shape syntax: A@{ shape: cyl, label: "Orders DB" }
        var at = Regex.Match(line[pos..], @"^@\{(?<body>[^}]*)\}");
        if (at.Success)
        {
            var body = at.Groups["body"].Value;
            hint = Regex.Match(body, @"shape\s*:\s*([\w-]+)").Groups[1].Value is { Length: > 0 } s ? (s is "cyl" or "db" or "database" or "das" ? "cylinder database" : s) : null;
            label = Regex.Match(body, @"label\s*:\s*""([^""]*)""").Groups[1].Value is { Length: > 0 } l ? l : null;
            pos += at.Length;
        }
        else
        {
            foreach (var (open, close, h) in Shapes)
            {
                if (string.CompareOrdinal(line, pos, open, 0, open.Length) != 0) continue;
                var start = pos + open.Length;
                var end = FindClose(line, start, close);
                if (end < 0) continue;
                label = line[start..end]; hint = h;
                pos = end + close.Length;
                break;
            }
        }
        var cls = Regex.Match(line[pos..], @"^:::[\w-]+");
        if (cls.Success) pos += cls.Length;

        // A node belongs to the subgraph where it is first mentioned (the usual way diagrams are written).
        var isNew = graph.Find(id) is null;
        var node = graph.Upsert(id, label is null ? null : Unquote(label), hint, lineNo);
        if (isNew && parent is not null && !node.IsContainer) node.ParentId = parent;
        return id;
    }

    private static int FindClose(string line, int start, string close)
    {
        var inQuote = false;
        for (var i = start; i <= line.Length - close.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            if (!inQuote && string.CompareOrdinal(line, i, close, 0, close.Length) == 0) return i;
        }
        return -1;
    }

    // ---------- architecture-beta ----------

    private static readonly Regex ArchElement = new(@"^(?<kind>group|service|junction)\s+(?<id>[\w-]+)(?:\((?<icon>[^)]*)\))?(?:\[(?<label>[^\]]*)\])?(?:\s+in\s+(?<parent>[\w-]+))?\s*$", RegexOptions.Compiled);
    private static readonly Regex ArchEdge = new(@"^(?<a>[\w-]+)(?:\{group\})?(?::[LRTB])?\s*(?<l><)?-+(?<r>>)?\s*(?:[LRTB]:)?(?<b>[\w-]+)(?:\{group\})?\s*$", RegexOptions.Compiled);

    private static void ParseArchitecture(string[] lines, int headerIndex, int firstLine, DiagramGraph graph)
    {
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            var lineNo = firstLine + i;
            var el = ArchElement.Match(line);
            if (el.Success)
            {
                if (el.Groups["kind"].Value == "junction") continue;
                var node = graph.Upsert(el.Groups["id"].Value, el.Groups["label"].Success ? el.Groups["label"].Value : el.Groups["id"].Value, el.Groups["icon"].Value, lineNo);
                node.IsContainer = el.Groups["kind"].Value == "group";
                if (el.Groups["parent"].Success) node.ParentId = el.Groups["parent"].Value;
                continue;
            }
            var ed = ArchEdge.Match(line);
            if (ed.Success)
            {
                var reverse = ed.Groups["l"].Success && !ed.Groups["r"].Success;
                graph.Edges.Add(new DiagramEdge
                {
                    SourceId = reverse ? ed.Groups["b"].Value : ed.Groups["a"].Value,
                    TargetId = reverse ? ed.Groups["a"].Value : ed.Groups["b"].Value, Line = lineNo,
                });
            }
        }
    }

    // ---------- C4 ----------

    private static void ParseC4(string[] lines, int headerIndex, int firstLine, DiagramGraph graph)
    {
        var parents = new Stack<string>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("title ", StringComparison.Ordinal)) { graph.Name ??= line[6..].Trim(); continue; }
            if (line == "}") { if (parents.Count > 0) parents.Pop(); continue; }
            C4Macros.TryApply(line, firstLine + i, graph, parents);
        }
    }

    // ---------- helpers ----------

    private static string StripComment(string line)
    {
        var i = line.IndexOf("%%", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    /// <summary>The "title:" of a YAML front-matter block (--- ... ---) at the top of a diagram, if any.</summary>
    private static string? FrontMatterTitle(string body)
    {
        var fm = Regex.Match(body, @"\A\s*---\s*\n(?<fm>[\s\S]*?)\n\s*---");
        if (!fm.Success) return null;
        var t = Regex.Match(fm.Groups["fm"].Value, @"^\s*title\s*:\s*(?<t>.+?)\s*$", RegexOptions.Multiline);
        return t.Success ? t.Groups["t"].Value.Trim('"', '\'') : null;
    }

    private static string StripFrontMatter(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("---")) return text;
        var end = t.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? text : t[(end + 4)..];
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        if (s.Length >= 2 && s[0] == '`' && s[^1] == '`') s = s[1..^1];
        return DiagramModelBuilder.StripHtml(s.Replace("\\n", " ").Replace("**", ""));
    }
}
