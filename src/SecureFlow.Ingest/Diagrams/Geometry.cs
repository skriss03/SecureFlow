namespace SecureFlow.Ingest.Diagrams;

/// <summary>Axis-aligned box in the diagram's own units (y may point up or down; only containment is used).</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;

    public bool Contains(Box o, double tolerance = 2) =>
        o.X >= X - tolerance && o.Y >= Y - tolerance && o.Right <= Right + tolerance && o.Bottom <= Bottom + tolerance && Area > o.Area;

    public bool ContainsPoint(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;

    /// <summary>Visio and similar tools store a centre point (pin) plus size.</summary>
    public static Box FromCenter(double cx, double cy, double w, double h) => new(cx - w / 2, cy - h / 2, w, h);
}

/// <summary>
/// For drawing tools without real grouping (Visio, Lucidchart exports, Excalidraw, Gliffy), people draw a zone
/// as a big box behind other shapes. A shape that fully encloses two or more other labelled shapes is treated as
/// a container, and every shape's parent is the smallest container that encloses it.
/// </summary>
internal static class Containment
{
    // Names people give to zones. A box that looks like a zone (dashed outline, or one of these words) is a
    // boundary even when it holds a single shape, e.g. a DMZ around one gateway.
    private static readonly System.Text.RegularExpressions.Regex ZoneWords = new(
        @"\b(zone|dmz|vpc|vnet|v-net|subnet|network|internet|intranet|on-?prem\w*|cloud|region|cluster|namespace|account|subscription|tenant|boundary|tier|layer|segment|perimeter|data ?cent(er|re)|environment|private|public)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool LooksLikeZone(DiagramNode n) =>
        (n.TypeHint ?? "").Contains("dashed", StringComparison.OrdinalIgnoreCase) || ZoneWords.IsMatch(n.Label);

    public static void Assign(IReadOnlyList<(DiagramNode Node, Box Box)> shapes, int minMembers = 2)
    {
        var candidates = shapes
            .Where(s => s.Node.ParentId is null && s.Box.Area > 0)
            .ToList();
        var containers = candidates
            .Where(c => candidates.Count(o => !ReferenceEquals(o.Node, c.Node) && c.Box.Contains(o.Box)) >= (LooksLikeZone(c.Node) ? 1 : minMembers))
            .ToList();
        foreach (var c in containers) c.Node.IsContainer = true;

        foreach (var s in candidates)
        {
            var parent = containers
                .Where(c => !ReferenceEquals(c.Node, s.Node) && c.Box.Contains(s.Box))
                .OrderBy(c => c.Box.Area)
                .FirstOrDefault();
            if (parent.Node is not null) s.Node.ParentId = parent.Node.Id;
        }
    }
}
