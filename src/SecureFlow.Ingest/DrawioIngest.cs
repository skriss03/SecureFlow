using SecureFlow.Core.Model;
using SecureFlow.Ingest.Diagrams;

namespace SecureFlow.Ingest;

/// <summary>
/// Deterministic draw.io (.drawio / .xml) parser. Needs no AI: shapes become components, edges become flows,
/// containers become trust boundaries. Kept for existing callers; the work is done by <see cref="DrawioParser"/>
/// and the shared <see cref="DiagramModelBuilder"/> that every diagram format uses.
/// </summary>
public static class DrawioIngest
{
    public static ArchitectureModel Parse(string xmlText, string? fileName = null)
    {
        try { return DiagramModelBuilder.Build(new DrawioParser().ParseXml(xmlText, fileName)); }
        catch (DiagramParseException ex) { throw new InvalidOperationException(ex.Message, ex); }
    }
}
