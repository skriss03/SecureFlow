using SecureFlow.Core.Model;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>Diagram import settings, bound from the "Diagrams" configuration section.</summary>
public sealed class DiagramOptions
{
    public const string Section = "Diagrams";

    /// <summary>Largest diagram file accepted.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    /// <summary>When a structured parser finds no shapes, send the file to the AI instead of failing.</summary>
    public bool AiFallback { get; set; } = true;
    /// <summary>Send text files in formats we don't parse (and plain SVG) to the AI to read.</summary>
    public bool AiTextFallback { get; set; } = true;
    /// <summary>Upper bound on text sent to the AI for one diagram (SVG markup can be large).</summary>
    public int MaxAiTextChars { get; set; } = 200_000;
    /// <summary>Format ids to turn off: drawio, mermaid, plantuml, structurizr, visio, excalidraw, gliffy, image, pdf, svg, text.</summary>
    public string[] DisabledFormats { get; set; } = [];
    /// <summary>
    /// Extra words that identify a component type, checked before the built-in ones. Keys are component types
    /// (Database, Queue, Cache, Gateway, External, ...), e.g. { "Database": ["oracle", "hana"] }.
    /// </summary>
    public Dictionary<string, string[]> ExtraTypeKeywords { get; set; } = new();

    public bool IsEnabled(string format) => !DisabledFormats.Contains(format, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<ComponentType, string[]> TypeKeywords() =>
        ExtraTypeKeywords
            .Where(kv => Enum.TryParse<ComponentType>(kv.Key, true, out _))
            .ToDictionary(kv => Enum.Parse<ComponentType>(kv.Key, true), kv => kv.Value);
}
