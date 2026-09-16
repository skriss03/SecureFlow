using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureFlow.Ai;

/// <summary>Serialization conventions shared by every provider, plus the embedded prompt/schema loaders.</summary>
public static class AiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never, // nulls mean "unknown" and the model should see them
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Tolerant parse: strips code fences and leading prose if a provider ignores the schema.</summary>
    public static T Parse<T>(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiResponseException("Provider returned no JSON object.", text);
        s = s[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize<T>(s, Options) ?? throw new AiResponseException("Provider returned null JSON.", text);
        }
        catch (JsonException ex)
        {
            throw new AiResponseException($"Provider returned invalid JSON: {ex.Message}", text);
        }
    }

    private static readonly Assembly Asm = typeof(AiJson).Assembly;

    public static string Prompt(string name) => Resource($"SecureFlow.Ai.Prompts.{name}.md");
    public static string SchemaText(string name) => Resource($"SecureFlow.Ai.Schemas.{name}.json");
    public static Dictionary<string, JsonElement> Schema(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaText(name))!;

    private static string Resource(string fullName)
    {
        using var stream = Asm.GetManifestResourceStream(fullName)
            ?? throw new FileNotFoundException($"Embedded resource '{fullName}' not found. Available: {string.Join(", ", Asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class AiResponseException : Exception
{
    public string RawResponse { get; }
    public AiResponseException(string message, string raw) : base(message) => RawResponse = raw;
}

public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }
}
