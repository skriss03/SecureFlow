using System.ClientModel;
using OpenAI.Chat;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai.Providers;

/// <summary>
/// Secondary provider behind the same interface. Selected with Ai:Provider = "openai".
/// Uses the official OpenAI SDK with strict JSON-schema response format.
/// </summary>
public sealed class OpenAiArchitectureAi : IArchitectureAi
{
    private readonly ChatClient _client;
    private readonly AiOptions _options;

    public OpenAiArchitectureAi(AiOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.ResolvedOpenAiKey))
            throw new AiUnavailableException("OPENAI_API_KEY is not set (or Ai:OpenAiApiKey in configuration).");
        _client = new ChatClient(options.OpenAiModel, options.ResolvedOpenAiKey);
    }

    public string Name => $"{_options.OpenAiModel} (OpenAI)";

    public async Task<ArchitectureModel> ExtractFromImageAsync(byte[] image, string mediaType, string? hint, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.ExtractImage(hint);
        var user = new UserChatMessage(
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image), mediaType),
            ChatMessageContentPart.CreateTextPart(req.UserText));
        progress?.Report($"Reading the diagram with {Name}...");
        var text = await CallAsync(req, user, progress, ct);
        return AnthropicArchitectureAi.Normalize(AiJson.Parse<ArchitectureModel>(text));
    }

    public async Task<ArchitectureModel> ExtractFromRepoDigestAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.ExtractRepo(digest);
        progress?.Report($"Reconstructing the architecture from {digest.Files.Count} files with {Name}...");
        var text = await CallAsync(req, new UserChatMessage(req.UserText), progress, ct);
        return AnthropicArchitectureAi.Normalize(AiJson.Parse<ArchitectureModel>(text));
    }

    public async Task<AiAnalysis> AnalyzeAsync(ArchitectureModel model, IReadOnlyList<Finding> ruleFindings, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.Analyze(model, ruleFindings);
        progress?.Report($"Reviewing {ruleFindings.Count} rule findings with {Name}...");
        var text = await CallAsync(req, new UserChatMessage(req.UserText), progress, ct);
        return AiJson.Parse<AiAnalysis>(text);
    }

    public async Task<FixProposal> ProposeFixAsync(ArchitectureModel model, Finding target, IReadOnlyList<Finding> allFindings, RepoDigest? digest, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.Fix(model, target, allFindings, digest);
        progress?.Report($"Designing a fix for \"{target.Title}\" with {Name}...");
        var text = await CallAsync(req, new UserChatMessage(req.UserText), progress, ct);
        return AiJson.Parse<AiFixResponse>(text).ToProposal(target.Id);
    }

    private async Task<string> CallAsync(RequestBuilder.Request req, UserChatMessage user, IProgress<string>? progress, CancellationToken ct)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: req.SchemaName.Replace('-', '_'),
                jsonSchema: BinaryData.FromString(AiJson.SchemaText(req.SchemaName)),
                jsonSchemaIsStrict: true),
        };
        var started = DateTimeOffset.UtcNow;
        try
        {
            ChatCompletion completion = await _client.CompleteChatAsync(
                [new SystemChatMessage(req.System), user], options, ct);
            progress?.Report($"{req.Operation} completed in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s.");
            if (completion.FinishReason == ChatFinishReason.ContentFilter)
                throw new AiResponseException("OpenAI content filter blocked the response.", "");
            if (completion.FinishReason == ChatFinishReason.Length)
                throw new AiResponseException("The response was cut off; raise the output limit.", completion.Content[0].Text);
            return completion.Content[0].Text;
        }
        catch (ClientResultException ex) when (ex.Status is 401 or 403)
        {
            throw new AiUnavailableException($"OpenAI rejected the API key: {ex.Message}");
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            throw new AiUnavailableException($"OpenAI rate limit hit: {ex.Message}");
        }
        catch (ClientResultException ex) when (ex.Status >= 500)
        {
            throw new AiUnavailableException($"OpenAI service error: {ex.Message}");
        }
    }
}
