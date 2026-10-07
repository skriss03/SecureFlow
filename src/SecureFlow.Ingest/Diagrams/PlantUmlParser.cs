using System.Text.RegularExpressions;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// PlantUML component and deployment diagrams, and C4-PlantUML (Person, Container, ContainerDb, Rel,
/// System_Boundary, ...). Elements become components, nested blocks become trust boundaries, arrows become flows.
/// </summary>
public sealed class PlantUmlParser : IDiagramParser
{
    public string Format => "plantuml";
    public string Tools => "PlantUML and C4-PlantUML";
    public IReadOnlyList<string> Extensions { get; } = [".puml", ".plantuml", ".pu", ".iuml", ".wsd"];

    public bool Sniff(string fileName, byte[] content) =>
        DiagramText.Head(content, 64 * 1024).Contains("@startuml", StringComparison.OrdinalIgnoreCase);

    // Element keywords. Container-ish ones become boundaries when followed by "{".
    private static readonly Regex Element = new(
        @"^(?<kw>actor|agent|artifact|boundary|card|circle|cloud|collections|component|control|database|entity|file|folder|frame|hexagon|interface|node|package|person|queue|rectangle|stack|storage|usecase)\s+" +
        @"(?:""(?<label>[^""]*)""|(?<name>[^\s{<#""]+))" +
        @"(?:\s+as\s+(?:""(?<alias2>[^""]*)""|(?<alias>[\w.]+)))?" +
        @"(?:\s*<<(?<stereo>[^>]*)>>)?(?:\s*#\S+)?\s*(?<open>\{)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // [Orders API] as api  /  [Orders API]
    private static readonly Regex Bracket = new(@"^\[(?<label>[^\]]+)\](?:\s+as\s+(?<alias>[\w.]+))?(?:\s*<<(?<stereo>[^>]*)>>)?\s*$", RegexOptions.Compiled);
    // a --> b : label   (endpoints may be ids, [Label] or "Label"; arrow may carry [#color] and a direction)
    private static readonly Regex ArrowLine = new(
        @"^(?<a>\[[^\]]+\]|""[^""]+""|[\w.]+)\s*(?<arrow><?[-.]+(?:\[[^\]]*\])?(?:left|right|up|down|le|ri|l|r|u|d)?[-.]*>?)\s*(?<b>\[[^\]]+\]|""[^""]+""|[\w.]+)\s*(?::\s*(?<label>.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Skip = new(@"^(!|skinparam|hide|show|left to right|top to bottom|scale|header|footer|caption|legend|endlegend|LAYOUT_|SHOW_|HIDE_|Add\w*Tag|Update\w*Style|autonumber|@enduml)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public DiagramGraph Parse(byte[] content, string fileName)
    {
        var lines = DiagramText.Decode(content).Replace("\r\n", "\n").Split('\n');
        var graph = new DiagramGraph { Format = Format, FileName = fileName, HasLineNumbers = true };
        var parents = new Stack<string>();
        var inBlockComment = false;
        var inNote = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (inBlockComment) { if (line.Contains("'/")) inBlockComment = false; continue; }
            if (line.StartsWith("/'")) { inBlockComment = line.IndexOf("'/", 2, StringComparison.Ordinal) < 0; continue; }
            if (inNote) { if (Regex.IsMatch(line, @"^end\s*note", RegexOptions.IgnoreCase)) inNote = false; continue; }
            if (line.Length == 0 || line.StartsWith('\'') || Skip.IsMatch(line) || line.StartsWith("@startuml", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(line, @"^note\b", RegexOptions.IgnoreCase)) { if (!line.Contains(':')) inNote = true; continue; }
            if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase)) { graph.Name ??= line[6..].Trim(); continue; }
            if (line == "}") { if (parents.Count > 0) parents.Pop(); continue; }
            var parent = parents.Count > 0 ? parents.Peek() : null;

            if (C4Macros.TryApply(line, lineNo, graph, parents)) continue;

            var em = Element.Match(line);
            if (em.Success)
            {
                var kw = em.Groups["kw"].Value.ToLowerInvariant();
                var label = em.Groups["label"].Success ? em.Groups["label"].Value : em.Groups["name"].Value;
                string id;
                if (em.Groups["alias"].Success) id = em.Groups["alias"].Value;                       // database "Orders DB" as db
                else if (em.Groups["alias2"].Success) { id = label; label = em.Groups["alias2"].Value; } // component api as "Orders API"
                else id = label;                                                                    // queue Events
                var node = graph.Upsert(id, CleanLabel(label), $"{kw} {em.Groups["stereo"].Value}".Trim(), lineNo);
                node.ParentId ??= parent;
                // Anything with a body ({ ... }) groups other elements: it becomes a boundary, not a component.
                if (em.Groups["open"].Success) { node.IsContainer = true; parents.Push(id); }
                continue;
            }

            var bm = Bracket.Match(line);
            if (bm.Success)
            {
                var label = bm.Groups["label"].Value;
                var id = bm.Groups["alias"].Success ? bm.Groups["alias"].Value : "[" + label + "]";
                graph.Upsert(id, CleanLabel(label), $"component {bm.Groups["stereo"].Value}".Trim(), lineNo).ParentId ??= parent;
                continue;
            }

            var am = ArrowLine.Match(line);
            if (am.Success && IsArrow(am.Groups["arrow"].Value))
            {
                var a = Endpoint(am.Groups["a"].Value, lineNo, graph, parent);
                var b = Endpoint(am.Groups["b"].Value, lineNo, graph, parent);
                var arrow = am.Groups["arrow"].Value;
                var reverse = arrow.StartsWith('<') && !arrow.EndsWith('>');
                graph.Edges.Add(new DiagramEdge
                {
                    SourceId = reverse ? b : a, TargetId = reverse ? a : b,
                    Label = CleanLabel(am.Groups["label"].Value), Dashed = arrow.Contains('.'), Line = lineNo,
                });
            }
        }
        return graph;
    }

    private static bool IsArrow(string arrow)
    {
        var core = Regex.Replace(arrow, @"\[[^\]]*\]|left|right|up|down|le|ri", "", RegexOptions.IgnoreCase).Trim('<', '>');
        return core.Length >= 1 && (arrow.Contains('>') || arrow.Contains('<') || core.Length >= 2);
    }

    /// <summary>"[Label]" and "\"Label\"" endpoints refer to (or create) a component by label; plain words are aliases.</summary>
    private static string Endpoint(string raw, int lineNo, DiagramGraph graph, string? parent)
    {
        if (raw.StartsWith('['))
        {
            var label = raw[1..^1];
            var existing = graph.Nodes.FirstOrDefault(n => n.Label == CleanLabel(label) && !n.IsContainer);
            if (existing is not null) return existing.Id;
            var node = graph.Upsert(raw, CleanLabel(label), "component", lineNo);
            node.ParentId ??= parent;
            return node.Id;
        }
        if (raw.StartsWith('"'))
        {
            var label = raw.Trim('"');
            var existing = graph.Nodes.FirstOrDefault(n => n.Label == CleanLabel(label));
            return existing?.Id ?? graph.Upsert(raw, CleanLabel(label), null, lineNo).Id;
        }
        var byId = graph.Find(raw);
        if (byId is null) graph.Upsert(raw, raw, null, lineNo).ParentId ??= parent;
        return raw;
    }

    private static string CleanLabel(string s) => DiagramModelBuilder.StripHtml(s.Replace("\\n", " ").Trim().Trim('"'));
}
