namespace SecureFlow.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>anthropic | openai | foundry</summary>
    public string Provider { get; set; } = "anthropic";
    public string AnthropicModel { get; set; } = "claude-opus-5";
    /// <summary>Verify the exact id in your OpenAI account; models are renamed often.</summary>
    public string OpenAiModel { get; set; } = "gpt-5.6";
    /// <summary>Falls back to ANTHROPIC_API_KEY / OPENAI_API_KEY environment variables.</summary>
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public int MaxOutputTokens { get; set; } = 32000;

    /// <summary>
    /// Microsoft Foundry deployment of Claude. Not a secret: the <c>&lt;resource-name&gt;</c> in
    /// https://&lt;resource-name&gt;.services.ai.azure.com/anthropic, safe in appsettings.json.
    /// The API key is still a secret -- reuses <see cref="AnthropicApiKey"/> / ANTHROPIC_API_KEY,
    /// since a Foundry key is "the Anthropic key" from the app's point of view, just routed differently.
    /// </summary>
    public string? FoundryResourceName { get; set; }
    /// <summary>The deployment name chosen in the Foundry portal. Used as the `model` parameter instead of AnthropicModel.</summary>
    public string? FoundryDeploymentName { get; set; }

    /// <summary>Directory for the replay cache. Relative paths resolve against the content root.</summary>
    public string CacheDir { get; set; } = "data/cache";
    public bool CacheEnabled { get; set; } = true;
    /// <summary>When true, never call a provider; serve from cache or fail. Also set by SECUREFLOW_AI_OFFLINE=1.</summary>
    public bool OfflineOnly { get; set; } = false;

    // Trimmed because a key pasted into a terminal or secrets file often carries a stray space or newline,
    // which providers reject as an invalid key. Quotes are stripped for the same reason.
    public string ResolvedAnthropicKey => Clean(AnthropicApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    public string ResolvedOpenAiKey => Clean(OpenAiApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    private static string Clean(string? key) => (key ?? "").Trim().Trim('"', '\'').Trim();

    /// <summary>
    /// Explains the usual reason a key is rejected, from its shape alone (never logs the key). Anthropic's own API
    /// keys start with "sk-ant-"; keys from Microsoft Foundry and other gateways do not, and need Ai:Provider=foundry.
    /// </summary>
    public string? AnthropicKeyHint(string provider)
    {
        var key = ResolvedAnthropicKey;
        if (key.Length == 0) return null;
        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("sk-ant-", StringComparison.Ordinal))
            return $"The configured key ({key.Length} characters) does not start with 'sk-ant-', so it is not an Anthropic API key. " +
                   "If it came from Microsoft Foundry, set Ai:Provider=foundry plus Ai:FoundryResourceName and Ai:FoundryDeploymentName; " +
                   "otherwise create a key at console.anthropic.com.";
        if (provider.Equals("foundry", StringComparison.OrdinalIgnoreCase) && key.StartsWith("sk-ant-", StringComparison.Ordinal))
            return "The configured key looks like an Anthropic API key (sk-ant-...), but Ai:Provider is 'foundry'. Use Ai:Provider=anthropic.";
        return null;
    }
    public bool ResolvedOffline => OfflineOnly || Environment.GetEnvironmentVariable("SECUREFLOW_AI_OFFLINE") is "1" or "true";
}
