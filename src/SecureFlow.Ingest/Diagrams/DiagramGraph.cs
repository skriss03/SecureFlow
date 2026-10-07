namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Tool-neutral shape of a diagram: boxes, arrows and containers, before any architecture meaning is applied.
/// Every format parser produces one of these, and <see cref="DiagramModelBuilder"/> turns it into the
/// architecture model the same way for all of them, so a Visio file and a Mermaid file of the same system
/// give the same components, flows, boundaries and findings.
/// </summary>
public sealed class DiagramGraph
{
    /// <summary>Format id, e.g. "drawio", "mermaid", "visio". Becomes the evidence source and the project source.</summary>
    public required string Format { get; init; }
    public string? FileName { get; init; }
    /// <summary>Diagram title when the file has one; otherwise the builder uses the file name.</summary>
    public string? Name { get; set; }
    /// <summary>True for text formats (Mermaid, PlantUML, Structurizr) whose nodes carry a source line number.</summary>
    public bool HasLineNumbers { get; init; }

    public List<DiagramNode> Nodes { get; } = new();
    public List<DiagramEdge> Edges { get; } = new();
    /// <summary>Notes for the model's assumptions, e.g. elements the parser skipped.</summary>
    public List<string> Notes { get; } = new();

    public DiagramNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>Adds a node, or returns the existing one with that id (first definition wins, later ones fill gaps).</summary>
    public DiagramNode Upsert(string id, string? label = null, string? typeHint = null, int? line = null)
    {
        var node = Find(id);
        if (node is null)
        {
            node = new DiagramNode { Id = id, Label = label ?? id, TypeHint = typeHint, Line = line };
            Nodes.Add(node);
            return node;
        }
        if (!string.IsNullOrWhiteSpace(label) && (node.Label == node.Id || string.IsNullOrWhiteSpace(node.Label))) node.Label = label;
        if (!string.IsNullOrWhiteSpace(typeHint) && string.IsNullOrWhiteSpace(node.TypeHint)) node.TypeHint = typeHint;
        node.Line ??= line;
        return node;
    }
}

public sealed class DiagramNode
{
    /// <summary>Id inside the source file (cell id, Mermaid id, shape id...). Not the final component id.</summary>
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    /// <summary>Whatever the tool says about the shape: style string, stencil, master name, macro name, tags.</summary>
    public string? TypeHint { get; set; }
    /// <summary>Technology text, when the format has a dedicated field for it (C4, Structurizr).</summary>
    public string? Technology { get; set; }
    /// <summary>Free description (C4, Structurizr); scanned for annotations like "x3" or "multi-AZ".</summary>
    public string? Description { get; set; }
    public string? ParentId { get; set; }
    /// <summary>Group, subgraph, swimlane, frame or boundary: becomes a trust boundary, never a component.</summary>
    public bool IsContainer { get; set; }
    public int? Line { get; set; }
}

public sealed class DiagramEdge
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public string Label { get; set; } = "";
    /// <summary>Dashed or dotted line: read as asynchronous, the common convention in every tool.</summary>
    public bool Dashed { get; set; }
    /// <summary>Raw style text for formats that encode meaning there (draw.io).</summary>
    public string? Style { get; set; }
    public int? Line { get; set; }
}
