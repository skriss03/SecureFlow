using SecureFlow.Core.Samples;
using SecureFlow.Ingest;

namespace SecureFlow.Api.Projects;

/// <summary>
/// Creates the demo portfolio and scans it for real. Every project appears immediately as Queued so the
/// dashboard fills in straight away, then the scans run a couple at a time: a dozen concurrent shallow
/// clones plus a dozen concurrent AI extractions would stall on disk and hit provider rate limits.
/// </summary>
public sealed class DemoSeeder
{
    private const int Concurrency = 2;

    private readonly ProjectStore _store;
    private readonly GroupStore _groups;
    private readonly ProjectPipeline _pipeline;
    private readonly AiRegistry _ai;
    private readonly RepoIngest _ingest;
    private readonly ILogger<DemoSeeder> _log;
    private int _running;

    public DemoSeeder(ProjectStore store, GroupStore groups, ProjectPipeline pipeline, AiRegistry ai, RepoIngest ingest, ILogger<DemoSeeder> log)
    {
        _store = store;
        _groups = groups;
        _pipeline = pipeline;
        _ai = ai;
        _ingest = ingest;
        _log = log;
    }

    public bool IsRunning => Volatile.Read(ref _running) > 0;

    /// <summary>Adds any catalogue entry that is not present yet and starts its scan. Returns the new projects.</summary>
    public IReadOnlyList<Project> Seed()
    {
        var existing = _store.All.ToList();
        var created = new List<(Project Project, DemoCatalog.Entry Entry)>();

        foreach (var entry in DemoCatalog.Entries)
        {
            var already = existing.Any(p => entry.RepoUrl is not null
                ? string.Equals(p.SourceRef, entry.RepoUrl, StringComparison.OrdinalIgnoreCase)
                : string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (already) continue;

            var group = _groups.All.FirstOrDefault(g => g.Name == entry.Group) ?? _groups.Add(entry.Group);
            var p = new Project
            {
                Name = entry.Name,
                Source = entry.RepoUrl is null ? "sample" : "repo",
                SourceRef = entry.RepoUrl ?? entry.Sample,
                GroupId = group.Id,
                AiProvider = _ai.Name,
            };
            p.AddLog("Queued as part of the demo portfolio.");
            _store.Add(p);
            created.Add((p, entry));
        }

        if (created.Count > 0) _ = Task.Run(() => RunAllAsync(created));
        return created.Select(c => c.Project).ToList();
    }

    private async Task RunAllAsync(List<(Project Project, DemoCatalog.Entry Entry)> work)
    {
        Interlocked.Increment(ref _running);
        try
        {
            using var gate = new SemaphoreSlim(Concurrency);
            await Task.WhenAll(work.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    await _pipeline.RunAsync(item.Project, Extractor(item.Project, item.Entry), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Demo seed failed for {Name}", item.Entry.Name);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private ProjectPipeline.Extractor Extractor(Project p, DemoCatalog.Entry entry)
    {
        if (entry.RepoUrl is null)
            return (progress, _) =>
            {
                progress.Report($"Building the built-in {entry.Sample} sample model.");
                return Task.FromResult(SampleModels.ShopFast());
            };

        return async (progress, ct) =>
        {
            if (!_ai.Available) throw new Ai.AiUnavailableException(_ai.Error ?? "No AI provider configured.");
            var digest = await _ingest.BuildAsync(entry.RepoUrl, null, progress, ct);
            p.Digest = digest;
            return await _ai.Ai!.ExtractFromRepoDigestAsync(digest, progress, ct);
        };
    }
}
