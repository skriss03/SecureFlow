using System.Text;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>A deterministic reader for one diagram file format. Parsers never call the AI.</summary>
public interface IDiagramParser
{
    /// <summary>Stable id, e.g. "mermaid". Used in configuration (Diagrams:DisabledFormats) and as the project source.</summary>
    string Format { get; }
    /// <summary>Tools whose files or exports this parser reads, for the UI and docs.</summary>
    string Tools { get; }
    /// <summary>File extensions, lower case with the dot. Multi-part ones (".drawio.svg") are listed too.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>True when the content (not just the extension) looks like this format.</summary>
    bool Sniff(string fileName, byte[] content);

    /// <summary>Reads the file into a tool-neutral graph. Throws <see cref="DiagramParseException"/> on malformed input.</summary>
    DiagramGraph Parse(byte[] content, string fileName);
}

public sealed class DiagramParseException(string message, Exception? inner = null) : Exception(message, inner);

internal static class DiagramText
{
    /// <summary>Decodes text with BOM detection (UTF-8 default), so files saved by Windows tools read correctly.</summary>
    public static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The first part of the file as text, for cheap sniffing.</summary>
    public static string Head(byte[] content, int max = 4096) =>
        Decode(content.Length > max ? content[..max] : content);

    public static bool HasExtension(string fileName, IEnumerable<string> extensions) =>
        extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
