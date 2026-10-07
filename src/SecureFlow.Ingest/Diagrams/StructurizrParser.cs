using System.Text.RegularExpressions;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Structurizr DSL (C4 model as code). Reads the model block: person, softwareSystem, container, component, group,
/// relationships (a -> b "desc" "tech") and tags. A software system that contains containers becomes a boundary.
/// Views, styles and deployment environments are ignored.
/// </summary>
public sealed class StructurizrParser : IDiagramParser
{
    public string Format => "structurizr";
    public string Tools => "Structurizr DSL (C4 model)";
    public IReadOnlyList<string> Extensions { get; } = [".dsl"];

    public bool Sniff(string fileName, byte[] content) =>
        Regex.IsMatch(DiagramText.Head(content, 16 * 1024), @"^\s*workspace\b[^\n]*\{", RegexOptions.Multiline);

    private static readonly Regex ElementRx = new(@"^(?:(?<id>[\w.]+)\s*=\s*)?(?<kind>person|softwareSystem|container|component|group|element)\b(?<args>[^{]*?)\s*(?<open>\{)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RelationRx = new(@"^(?:(?<rid>[\w.]+)\s*=\s*)?(?<a>[\w.]+)?\s*->\s*(?<b>[\w.]+)(?<args>[^{]*?)\s*(?<open>\{)?\s*$", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedBlocks = new(StringComparer.OrdinalIgnoreCase)
        { "views", "styles", "configuration", "properties", "perspectives", "branding", "terminology", "deploymentEnvironment", "users", "!docs", "!adrs" };

    private sealed record Block(string Kind, string? ElementId);

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        var lines = DiagramText.Decode(content).Replace("\r\n", "\n").Split('\n');
        var graph = new DiagramGraph { Format = Format, FileName = fileName, HasLineNumbers = true };
        var stack = new Stack<Block>();
        var inComment = false;
        var anon = 0;

        string? CurrentElement() => stack.FirstOrDefault(b => b.ElementId is not null)?.ElementId;
        string Resolve(string id) =>
            graph.Find(id) is not null ? id : id.Contains('.') && graph.Find(id.Split('.')[^1]) is not null ? id.Split('.')[^1] : id;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (inComment) { if (line.Contains("*/")) inComment = false; continue; }
            if (line.StartsWith("/*")) { inComment = !line.Contains("*/"); continue; }
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#')) continue;

            if (line == "}") { if (stack.Count > 0) stack.Pop(); continue; }
            if (stack.Any(b => b.Kind == "skip"))
            {
                if (line.EndsWith('{')) stack.Push(new Block("skip", null));
                continue;
            }

            var keyword = line.Split(' ', '\t', '{')[0];
            if (keyword.Equals("workspace", StringComparison.OrdinalIgnoreCase))
            {
                if (Quoted.Match(line) is { Success: true } wn) graph.Name = wn.Groups[1].Value;
                if (line.EndsWith('{')) stack.Push(new Block("workspace", null));
                continue;
            }
            if (keyword.Equals("model", StringComparison.OrdinalIgnoreCase)) { if (line.EndsWith('{')) stack.Push(new Block("model", null)); continue; }
            if (SkippedBlocks.Contains(keyword)) { if (line.EndsWith('{')) stack.Push(new Block("skip", null)); continue; }
            if (keyword.StartsWith('!')) { if (line.EndsWith('{')) stack.Push(new Block("skip", null)); continue; }

            // Properties of the element whose block we are in.
            var current = CurrentElement();
            if (current is not null && Regex.Match(line, @"^(tags|technology|description)\s+(.*)$", RegexOptions.IgnoreCase) is { Success: true } pm)
            {
                var node = graph.Find(current)!;
                var values = Quoted.Matches(pm.Groups[2].Value).Select(q => q.Groups[1].Value).ToList();
                var value = string.Join(" ", values.Count > 0 ? values : [pm.Groups[2].Value.Trim()]);
                switch (pm.Groups[1].Value.ToLowerInvariant())
                {
                    case "tags": node.TypeHint = $"{node.TypeHint} {value}".Trim(); break;
                    case "technology": node.Technology = value; break;
                    case "description": node.Description = value; break;
                }
                continue;
            }

            var rm = RelationRx.Match(line);
            if (rm.Success && line.Contains("->"))
            {
                var from = rm.Groups["a"].Success && rm.Groups["a"].Value != "this" ? rm.Groups["a"].Value : current;
                if (from is not null)
                {
                    var args = Quoted.Matches(rm.Groups["args"].Value).Select(q => q.Groups[1].Value).ToList();
                    var tags = args.Count > 2 ? args[2] : "";
                    graph.Edges.Add(new DiagramEdge
                    {
                        SourceId = from, TargetId = rm.Groups["b"].Value, Line = lineNo,
                        Label = string.Join(" ", args.Take(2).Where(a => a.Length > 0)),
                        Dashed = tags.Contains("async", StringComparison.OrdinalIgnoreCase),
                    });
                }
                if (rm.Groups["open"].Success) stack.Push(new Block("relationship", null));
                continue;
            }

            var em = ElementRx.Match(line);
            if (em.Success)
            {
                var kind = em.Groups["kind"].Value.ToLowerInvariant();
                var a = Quoted.Matches(em.Groups["args"].Value).Select(q => q.Groups[1].Value).ToList();
                string At(int k) => k < a.Count ? a[k] : "";
                var name = At(0);
                var id = em.Groups["id"].Success ? em.Groups["id"].Value : $"_{kind}{++anon}";
                // person/softwareSystem: name desc tags | container/component: name desc tech tags | group: name
                var (desc, tech, tags) = kind switch
                {
                    "container" or "component" => (At(1), At(2), At(3)),
                    "element" => (At(2), At(1), At(3)),
                    _ => (At(1), "", At(2)),
                };
                var hint = kind switch
                {
                    "person" => "person",
                    "softwaresystem" => "software system",
                    "group" => "group",
                    _ => kind,
                };
                var node = graph.Upsert(id, name.Length > 0 ? name : id, $"{hint} {tags}".Trim(), lineNo);
                node.Technology = tech.Length > 0 ? tech : node.Technology;
                node.Description = desc.Length > 0 ? desc : node.Description;
                node.ParentId ??= CurrentElement();
                if (kind == "group") node.IsContainer = true;
                if (em.Groups["open"].Success) stack.Push(new Block("element", id));
            }
        }

        // Anything that ended up holding other elements (a software system with containers) is a boundary.
        var parentIds = graph.Nodes.Where(n => n.ParentId is not null).Select(n => n.ParentId!).ToHashSet();
        foreach (var n in graph.Nodes.Where(n => parentIds.Contains(n.Id))) n.IsContainer = true;
        foreach (var e in graph.Edges.ToList())
        {
            var fixedEdge = new DiagramEdge { SourceId = Resolve(e.SourceId), TargetId = Resolve(e.TargetId), Label = e.Label, Dashed = e.Dashed, Line = e.Line };
            graph.Edges[graph.Edges.IndexOf(e)] = fixedEdge;
        }
        // External systems in Structurizr are usually tagged "External" or "Existing System".
        foreach (var n in graph.Nodes.Where(n => (n.TypeHint ?? "").Contains("existing system", StringComparison.OrdinalIgnoreCase)))
            n.TypeHint += " external";
        return graph;
    }
}
