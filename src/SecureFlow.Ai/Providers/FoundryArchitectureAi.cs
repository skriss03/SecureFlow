using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Foundry;
using Anthropic.Models.Messages;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai.Providers;

/// <summary>
/// Claude reached through a Microsoft Foundry deployment instead of Anthropic's own API.
/// Same request/response shape as <see cref="AnthropicArchitectureAi"/>; only the client construction
/// and the model identifier (a Foundry deployment name, not a raw Claude model id) differ.
/// </summary>
public sealed class FoundryArchitectureAi : IArchitectureAi
{
    private readonly AnthropicFoundryClient _client;
    private readonly AiOptions _options;

    public FoundryArchitectureAi(AiOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.ResolvedAnthropicKey))
            throw new AiUnavailableException("No Foundry API key set (ANTHROPIC_API_KEY or Ai:AnthropicApiKey in configuration).");
        if (string.IsNullOrWhiteSpace(options.FoundryResourceName))
            throw new AiUnavailableException("Ai:FoundryResourceName is not set (the <resource-name> from https://<resource-name>.services.ai.azure.com/anthropic).");
        if (string.IsNullOrWhiteSpace(options.FoundryDeploymentName))
            throw new AiUnavailableException("Ai:FoundryDeploymentName is not set (the deployment name chosen in the Foundry portal).");

        _client = new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(options.ResolvedAnthropicKey, options.FoundryResourceName));
    }

    public string Name => $"{_options.FoundryDeploymentName} (Microsoft Foundry)";

    public async Task<ArchitectureModel> ExtractFromImageAsync(byte[] image, string mediaType, string? hint, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.ExtractImage(hint);
        var content = new List<ContentBlockParam>
        {
            new ImageBlockParam { Source = new Base64ImageSource { MediaType = mediaType, Data = Convert.ToBase64String(image) } },
            new TextBlockParam { Text = req.UserText },
        };
        progress?.Report($"Reading the diagram with {Name}...");
        var text = await CallAsync(req, content, progress, ct);
        return AnthropicArchitectureAi.Normalize(AiJson.Parse<ArchitectureModel>(text));
    }

    public async Task<ArchitectureModel> ExtractFromRepoDigestAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.ExtractRepo(digest);
        progress?.Report($"Reconstructing the architecture from {digest.Files.Count} files with {Name}...");
        var text = await CallAsync(req, [new TextBlockParam { Text = req.UserText }], progress, ct);
        return AnthropicArchitectureAi.Normalize(AiJson.Parse<ArchitectureModel>(text));
    }

    public async Task<AiAnalysis> AnalyzeAsync(ArchitectureModel model, IReadOnlyList<Finding> ruleFindings, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.Analyze(model, ruleFindings);
        progress?.Report($"Reviewing {ruleFindings.Count} rule findings for context and priority with {Name}...");
        var text = await CallAsync(req, [new TextBlockParam { Text = req.UserText }], progress, ct);
        return AiJson.Parse<AiAnalysis>(text);
    }

    public async Task<FixProposal> ProposeFixAsync(ArchitectureModel model, Finding target, IReadOnlyList<Finding> allFindings, RepoDigest? digest, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.Fix(model, target, allFindings, digest);
        progress?.Report($"Designing a fix for \"{target.Title}\" with {Name}...");
        var text = await CallAsync(req, [new TextBlockParam { Text = req.UserText }], progress, ct);
        return AiJson.Parse<AiFixResponse>(text).ToProposal(target.Id);
    }

    private async Task<string> CallAsync(RequestBuilder.Request req, List<ContentBlockParam> content, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return await CallOnceAsync(req, content, useSchema: true, progress, ct);
        }
        catch (AnthropicBadRequestException ex) when (ex.Message.Contains("schema", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("output_config", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("Structured output schema was rejected; retrying with prompt-only JSON.");
            var fallback = new List<ContentBlockParam>(content)
            {
                new TextBlockParam { Text = "\n\nRespond with only a JSON object that matches this JSON schema, no prose:\n" + AiJson.SchemaText(req.SchemaName) },
            };
            return await CallOnceAsync(req, fallback, useSchema: false, progress, ct);
        }
    }

    private async Task<string> CallOnceAsync(RequestBuilder.Request req, List<ContentBlockParam> content, bool useSchema, IProgress<string>? progress, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            // Foundry routes by deployment name, not the raw Claude model id.
            Model = _options.FoundryDeploymentName!,
            MaxTokens = _options.MaxOutputTokens,
            System = req.System,
            OutputConfig = useSchema
                ? new OutputConfig { Format = new JsonOutputFormat { Schema = AiJson.Schema(req.SchemaName) } }
                : null,
            Messages = [new() { Role = Role.User, Content = content }],
        };

        var sb = new StringBuilder();
        string? stopReason = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var ev in _client.Messages.CreateStreaming(parameters, cancellationToken: ct))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t))
                {
                    sb.Append(t.Text);
                    if (sb.Length % 4000 < t.Text.Length) progress?.Report($"...{sb.Length / 1000}k characters received");
                }
                else if (ev.TryPickDelta(out var md))
                {
                    stopReason = md.Delta.StopReason?.ToString();
                }
            }
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AiUnavailableException($"Foundry rate limit hit: {ex.Message}");
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AiUnavailableException($"Foundry rejected the API key: {ex.Message}");
        }
        catch (Anthropic5xxException ex)
        {
            throw new AiUnavailableException($"Foundry service error: {ex.Message}");
        }
        catch (AnthropicIOException ex)
        {
            throw new AiUnavailableException($"Could not reach Foundry: {ex.Message}");
        }

        progress?.Report($"{req.Operation} completed in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s.");
        if (stopReason is "refusal")
            throw new AiResponseException("The model declined this request (stop_reason=refusal).", sb.ToString());
        if (stopReason is "max_tokens")
            throw new AiResponseException("The response was cut off at max_tokens; raise Ai:MaxOutputTokens.", sb.ToString());
        return sb.ToString();
    }
}
