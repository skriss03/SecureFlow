namespace SecureFlow.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>anthropic | openai</summary>
    public string Provider { get; set; } = "anthropic";
    public string AnthropicModel { get; set; } = "claude-opus-5";
    /// <summary>Verify the exact id in your OpenAI account; models are renamed often.</summary>
    public string OpenAiModel { get; set; } = "gpt-5.6";
    /// <summary>Falls back to ANTHROPIC_API_KEY / OPENAI_API_KEY environment variables.</summary>
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public int MaxOutputTokens { get; set; } = 32000;

    /// <summary>Directory for the replay cache. Relative paths resolve against the content root.</summary>
    public string CacheDir { get; set; } = "data/cache";
    public bool CacheEnabled { get; set; } = true;
    /// <summary>When true, never call a provider; serve from cache or fail. Also set by SECUREFLOW_AI_OFFLINE=1.</summary>
    public bool OfflineOnly { get; set; } = false;

    public string ResolvedAnthropicKey => AnthropicApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
    public string ResolvedOpenAiKey => OpenAiApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
    public bool ResolvedOffline => OfflineOnly || Environment.GetEnvironmentVariable("SECUREFLOW_AI_OFFLINE") is "1" or "true";
}
