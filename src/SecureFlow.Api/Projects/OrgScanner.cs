using SecureFlow.Ai;
using SecureFlow.Ingest;

namespace SecureFlow.Api.Projects;

/// <summary>
/// Fans a GitHub org/user "root" URL out into one project per repository, scanned through the same
/// pipeline as a single repo scan. Bounded concurrency for the same reason as DemoSeeder: a dozen
/// concurrent shallow clones plus a dozen concurrent AI extractions would stall disk and rate limits.
/// </summary>
public sealed class OrgScanner
{
    private const int Concurrency = 2;

    private readonly ProjectStore _store;
    private readonly ProjectPipeline _pipeline;
    private readonly AiRegistry _ai;
    private readonly RepoIngest _ingest;
    private readonly GitHostDiscovery _discovery;
    private readonly ILogger<OrgScanner> _log;

    public OrgScanner(ProjectStore store, ProjectPipeline pipeline, AiRegistry ai, RepoIngest ingest, GitHostDiscovery discovery, ILogger<OrgScanner> log)
    {
        _store = store;
        _pipeline = pipeline;
        _ai = ai;
        _ingest = ingest;
        _discovery = discovery;
        _log = log;
    }

    /// <summary>Lists the org's repos, creates one Queued project per repo, and starts scanning in the background.</summary>
    public async Task<List<Project>> StartAsync(string owner, CancellationToken ct)
    {
        var repos = await _discovery.ListReposAsync(owner, ct);
        if (repos.Count == 0) throw new InvalidOperationException($"'{owner}' has no scannable repositories (excluding forks and archived).");

        var projects = new List<Project>();
        foreach (var r in repos)
        {
            var p = new Project { Name = r.Name, Source = "repo", SourceRef = r.CloneUrl, AiProvider = _ai.Name };
            p.AddLog($"Queued as part of scanning {owner} ({repos.Count} repositories).");
            _store.Add(p);
            projects.Add(p);
        }

        _ = Task.Run(() => RunAllAsync(projects, repos), CancellationToken.None);
        return projects;
    }

    private async Task RunAllAsync(List<Project> projects, List<DiscoveredRepo> repos)
    {
        using var gate = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(projects.Zip(repos, (p, r) => (p, r)).Select(async pair =>
        {
            await gate.WaitAsync();
            try
            {
                await _pipeline.RunAsync(pair.p, async (progress, ct) =>
                {
                    if (!_ai.Available) throw new AiUnavailableException(_ai.Error ?? "No AI provider configured.");
                    var digest = await _ingest.BuildAsync(pair.r.CloneUrl, pair.r.DefaultBranch, progress, ct);
                    pair.p.Digest = digest;
                    return await _ai.Ai!.ExtractFromRepoDigestAsync(digest, progress, ct);
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Org scan failed for {Repo}", pair.r.Name);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
