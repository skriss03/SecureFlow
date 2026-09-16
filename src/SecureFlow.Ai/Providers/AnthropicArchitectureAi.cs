using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai.Providers;

/// <summary>
/// Primary provider. Uses the official Anthropic SDK with JSON-schema structured output so responses parse first time,
/// adaptive thinking (the Claude Opus 5 default), and streaming so large repo digests never hit an HTTP timeout.
/// </summary>
public sealed class AnthropicArchitectureAi : IArchitectureAi
{
    private readonly AnthropicClient _client;
    private readonly AiOptions _options;

    public AnthropicArchitectureAi(AiOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.ResolvedAnthropicKey))
            throw new AiUnavailableException("ANTHROPIC_API_KEY is not set (or Ai:AnthropicApiKey in configuration).");
        _client = new AnthropicClient { ApiKey = options.ResolvedAnthropicKey };
    }

    public string Name => $"{_options.AnthropicModel} (Anthropic)";

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
        return Normalize(AiJson.Parse<ArchitectureModel>(text));
    }

    public async Task<ArchitectureModel> ExtractFromRepoDigestAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct)
    {
        var req = RequestBuilder.ExtractRepo(digest);
        progress?.Report($"Reconstructing the architecture from {digest.Files.Count} files with {Name}...");
        var text = await CallAsync(req, [new TextBlockParam { Text = req.UserText }], progress, ct);
        return Normalize(AiJson.Parse<ArchitectureModel>(text));
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
        var parameters = new MessageCreateParams
        {
            Model = _options.AnthropicModel,
            MaxTokens = _options.MaxOutputTokens,
            System = req.System,
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = AiJson.Schema(req.SchemaName) },
            },
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
            throw new AiUnavailableException($"Anthropic rate limit hit: {ex.Message}");
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AiUnavailableException($"Anthropic rejected the API key: {ex.Message}");
        }
        catch (Anthropic5xxException ex)
        {
            throw new AiUnavailableException($"Anthropic service error: {ex.Message}");
        }
        catch (AnthropicIOException ex)
        {
            throw new AiUnavailableException($"Could not reach Anthropic: {ex.Message}");
        }

        progress?.Report($"{req.Operation} completed in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s.");
        if (stopReason is "refusal")
            throw new AiResponseException("The model declined this request (stop_reason=refusal).", sb.ToString());
        if (stopReason is "max_tokens")
            throw new AiResponseException("The response was cut off at max_tokens; raise Ai:MaxOutputTokens.", sb.ToString());
        return sb.ToString();
    }

    /// <summary>Defensive cleanup so a slightly off response still produces a usable graph.</summary>
    internal static ArchitectureModel Normalize(ArchitectureModel m)
    {
        foreach (var c in m.Components)
        {
            c.Id = Slug(c.Id);
            c.Props ??= new ComponentProps();
            c.Evidence ??= new List<Evidence>();
        }
        var ids = m.Components.Select(c => c.Id).ToHashSet();
        var n = 0;
        foreach (var f in m.Flows)
        {
            f.From = Slug(f.From); f.To = Slug(f.To);
            if (string.IsNullOrWhiteSpace(f.Id)) f.Id = $"f{++n}";
            f.Evidence ??= new List<Evidence>();
        }
        m.Flows.RemoveAll(f => !ids.Contains(f.From) || !ids.Contains(f.To) || f.From == f.To);
        foreach (var b in m.TrustBoundaries)
        {
            b.ComponentIds = b.ComponentIds.Select(Slug).Where(ids.Contains).ToList();
            if (string.IsNullOrWhiteSpace(b.Id)) b.Id = Slug(b.Name);
        }
        m.TrustBoundaries.RemoveAll(b => b.ComponentIds.Count == 0);
        m.Assumptions ??= new List<string>();
        if (string.IsNullOrWhiteSpace(m.Name)) m.Name = "Extracted architecture";
        return m;
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in (s ?? "").Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
