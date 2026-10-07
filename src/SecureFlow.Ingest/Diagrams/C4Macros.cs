using System.Text;
using System.Text.RegularExpressions;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// The C4 model macros shared by C4-PlantUML and Mermaid's C4 diagrams:
/// Person(alias, "Label", "Descr"), Container(alias, "Label", "Techn", "Descr"), ContainerDb, ContainerQueue,
/// System_Ext, *_Boundary(alias, "Label") { ... }, Rel(from, to, "Label", "Techn") and their variants.
/// </summary>
internal static class C4Macros
{
    private static readonly Regex Call = new(@"^\s*(?<name>[A-Za-z_][\w]*)\s*\((?<args>.*)\)\s*(?<open>\{)?\s*$", RegexOptions.Compiled);

    /// <summary>Handles one line. Returns true when it was a C4 macro (element, boundary or relationship).</summary>
    public static bool TryApply(string line, int lineNo, DiagramGraph graph, Stack<string> parents)
    {
        var m = Call.Match(line);
        if (!m.Success) return false;
        var name = m.Groups["name"].Value;
        var (pos, named) = SplitArgs(m.Groups["args"].Value);
        string Arg(int i, string key) => named.TryGetValue(key, out var v) ? v : i < pos.Count ? pos[i] : "";
        var opens = m.Groups["open"].Success;
        var parent = parents.Count > 0 ? parents.Peek() : null;

        if (Regex.IsMatch(name, @"^(Bi)?Rel(_|$)", RegexOptions.IgnoreCase))
        {
            var from = Arg(0, "from"); var to = Arg(1, "to");
            if (from.Length == 0 || to.Length == 0) return true;
            var reverse = name.Contains("_Back", StringComparison.OrdinalIgnoreCase);
            var label = string.Join(" ", new[] { Arg(2, "label"), Arg(3, "techn") }.Where(s => s.Length > 0));
            var tags = named.GetValueOrDefault("tags", "");
            graph.Edges.Add(new DiagramEdge
            {
                SourceId = reverse ? to : from, TargetId = reverse ? from : to, Label = label, Line = lineNo,
                Dashed = tags.Contains("async", StringComparison.OrdinalIgnoreCase),
            });
            return true;
        }

        if (name.EndsWith("Boundary", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Deployment_Node", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("Node", StringComparison.OrdinalIgnoreCase) && opens))
        {
            var alias = Arg(0, "alias");
            if (alias.Length == 0) return true;
            var node = graph.Upsert(alias, Arg(1, "label") is { Length: > 0 } l ? l : alias, "boundary", lineNo);
            node.IsContainer = true;
            node.ParentId ??= parent;
            if (opens) parents.Push(alias);
            return true;
        }

        var kind = ElementKind(name);
        if (kind is null) return false;
        {
            var alias = Arg(0, "alias");
            if (alias.Length == 0) return true;
            var isPersonOrSystem = name.StartsWith("Person", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase);
            // Person/System: (alias, label, descr). Container/Component: (alias, label, techn, descr).
            var techn = isPersonOrSystem ? named.GetValueOrDefault("type", "") : Arg(2, "techn");
            var descr = isPersonOrSystem ? Arg(2, "descr") : Arg(3, "descr");
            var tags = named.GetValueOrDefault("tags", "");
            var node = graph.Upsert(alias, Arg(1, "label") is { Length: > 0 } l ? l : alias, $"{kind} {tags}".Trim(), lineNo);
            node.Technology = techn.Length > 0 ? techn : node.Technology;
            node.Description = descr.Length > 0 ? descr : node.Description;
            node.ParentId ??= parent;
            if (opens) { node.IsContainer = true; parents.Push(alias); }
            return true;
        }
    }

    /// <summary>Maps a macro name to words the type inference understands; null when it is not an element macro.</summary>
    private static string? ElementKind(string macro)
    {
        var m = macro.ToLowerInvariant();
        if (!(m.StartsWith("person") || m.StartsWith("system") || m.StartsWith("container") || m.StartsWith("component") || m.StartsWith("enterprise"))) return null;
        var sb = new StringBuilder();
        if (m.StartsWith("person")) sb.Append("person");
        else if (m.Contains("db")) sb.Append("database");
        else if (m.Contains("queue")) sb.Append("queue");
        else if (m.StartsWith("system") && m.Contains("_ext")) sb.Append("external system");
        else if (m.StartsWith("system")) sb.Append("system");
        else sb.Append(m.StartsWith("component") ? "component" : "container");
        if (m.Contains("_ext") && !m.StartsWith("person") && !sb.ToString().Contains("external")) sb.Append(" external");
        return sb.ToString();
    }

    /// <summary>Splits "a, \"b, c\", $tags=\"x\"" into positional and $named arguments, honouring quotes.</summary>
    internal static (List<string> Positional, Dictionary<string, string> Named) SplitArgs(string args)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var depth = 0;
        foreach (var ch in args)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && ch == '(') depth++;
            if (!inQuote && ch == ')') depth--;
            if (ch == ',' && !inQuote && depth == 0) { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0 || parts.Count > 0) parts.Add(sb.ToString().Trim());

        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            var nm = Regex.Match(p, @"^\$(\w+)\s*=\s*(.*)$");
            if (nm.Success) named[nm.Groups[1].Value] = nm.Groups[2].Value.Trim();
            else positional.Add(p);
        }
        return (positional, named);
    }
}
