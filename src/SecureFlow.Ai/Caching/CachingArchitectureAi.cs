using System.Security.Cryptography;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai.Caching;

/// <summary>Decorator: any provider becomes replayable. Cache keys are content hashes of the real inputs.</summary>
public sealed class CachingArchitectureAi : IArchitectureAi
{
    private readonly IArchitectureAi _inner;
    private readonly ReplayCache _cache;

    public CachingArchitectureAi(IArchitectureAi inner, ReplayCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public string Name => _inner.Name;

    public Task<ArchitectureModel> ExtractFromImageAsync(byte[] image, string mediaType, string? hint, IProgress<string>? progress, CancellationToken ct) =>
        _cache.GetOrAddAsync("extract-image", Name,
            new { image = Convert.ToHexString(SHA256.HashData(image)), mediaType, hint, v = PromptVersion },
            () => _inner.ExtractFromImageAsync(image, mediaType, hint, progress, ct), progress, ct);

    public Task<ArchitectureModel> ExtractFromRepoDigestAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct) =>
        _cache.GetOrAddAsync("extract-repo", Name,
            new { digest.RepoUrl, digest.Commit, files = digest.Files.Select(f => new { f.Path, hash = Hash(f.Content) }), v = PromptVersion },
            () => _inner.ExtractFromRepoDigestAsync(digest, progress, ct), progress, ct);

    public Task<AiAnalysis> AnalyzeAsync(ArchitectureModel model, IReadOnlyList<Finding> ruleFindings, IProgress<string>? progress, CancellationToken ct) =>
        _cache.GetOrAddAsync("analyze", Name,
            new { model, findings = ruleFindings.Select(f => f.Id).Order(), v = PromptVersion },
            () => _inner.AnalyzeAsync(model, ruleFindings, progress, ct), progress, ct);

    public Task<FixProposal> ProposeFixAsync(ArchitectureModel model, Finding target, IReadOnlyList<Finding> allFindings, RepoDigest? digest, IProgress<string>? progress, CancellationToken ct) =>
        _cache.GetOrAddAsync("fix", Name,
            new { model, target = target.Id, others = allFindings.Select(f => f.Id).Order(), digest = digest?.Commit, v = PromptVersion },
            () => _inner.ProposeFixAsync(model, target, allFindings, digest, progress, ct), progress, ct);

    /// <summary>Bump when prompts change materially so stale cache entries are not replayed.</summary>
    private const int PromptVersion = 1;

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16];
}
