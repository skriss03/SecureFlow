using SecureFlow.Ai;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Scoring;

namespace SecureFlow.Api.Projects;

/// <summary>Holds the configured AI provider, or the reason there is none. The app works without one.</summary>
public sealed class AiRegistry
{
    public IArchitectureAi? Ai { get; init; }
    public string? Error { get; init; }
    public bool Available => Ai is not null;
    public string Name => Ai?.Name ?? "none";
}

/// <summary>
/// The core loop: extract → rules → score → AI review, then fix → apply → re-score.
/// Deterministic steps always run; AI steps are best-effort and log instead of failing the project.
/// </summary>
public sealed class ProjectPipeline
{
    private readonly ProjectStore _store;
    private readonly AiRegistry _ai;
    private readonly ILogger<ProjectPipeline> _log;

    public ProjectPipeline(ProjectStore store, AiRegistry ai, ILogger<ProjectPipeline> log)
    {
        _store = store;
        _ai = ai;
        _log = log;
    }

    public delegate Task<ArchitectureModel> Extractor(IProgress<string> progress, CancellationToken ct);

    /// <summary>Queues the project and runs the pipeline in the background. The caller returns the id and the UI follows the SSE log.</summary>
    public Project Start(Project project, Extractor extractor)
    {
        project.AiProvider = _ai.Name;
        _store.Add(project);
        _ = Task.Run(() => RunAsync(project, extractor, CancellationToken.None));
        return project;
    }

    public async Task RunAsync(Project p, Extractor extractor, CancellationToken ct)
    {
        var progress = new LogProgress(p);
        try
        {
            p.Status = ProjectStatus.Extracting;
            p.AddLog($"Started: source={p.Source} {p.SourceRef}");
            _store.Save(p);

            p.Model = await extractor(progress, ct);
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name == "Untitled") p.Name = p.Model.Name;
            p.AddLog($"Model ready: {p.Model.Components.Count} components, {p.Model.Flows.Count} flows, {p.Model.TrustBoundaries.Count} trust boundaries, {p.Model.Assumptions.Count} assumptions.");

            p.Status = ProjectStatus.Analyzing;
            Analyze(p, "Initial analysis");
            p.AddLog($"Rules: {p.Findings.Count} findings. Resilience {p.Score.Resilience} ({p.Score.ResilienceGrade}), Security {p.Score.Security} ({p.Score.SecurityGrade}).");
            _store.Save(p);

            await ReviewAsync(p, progress, ct);

            p.Status = ProjectStatus.Ready;
            p.AddLog("Done.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pipeline failed for project {Id}", p.Id);
            p.Status = ProjectStatus.Error;
            p.Error = ex.Message;
            p.AddLog("Failed: " + ex.Message, "error");
        }
        finally
        {
            _store.Save(p);
        }
    }

    /// <summary>Deterministic part: rules + score. Preserves statuses of findings that still exist and keeps fixed ones for history.</summary>
    public void Analyze(Project p, string action)
    {
        var previous = p.Findings.ToDictionary(f => f.Id);
        var fresh = RuleCatalog.Evaluate(p.Model);

        foreach (var f in fresh)
            if (previous.TryGetValue(f.Id, out var old) && old.Status == FindingStatus.Accepted)
                f.Status = FindingStatus.Accepted;

        var freshIds = fresh.Select(f => f.Id).ToHashSet();
        var aiFindings = p.Findings.Where(f => f.Source == FindingSource.Ai).ToList();
        var nowFixed = p.Findings.Where(f => f.Source == FindingSource.Rule && !freshIds.Contains(f.Id) && f.Status != FindingStatus.Fixed)
            .Select(f => { f.Status = FindingStatus.Fixed; return f; });
        var alreadyFixed = p.Findings.Where(f => f.Source == FindingSource.Rule && f.Status == FindingStatus.Fixed && !freshIds.Contains(f.Id));

        p.Findings = fresh.Concat(aiFindings).Concat(nowFixed).Concat(alreadyFixed)
            .OrderBy(f => f.Status).ThenByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToList();
        p.Score = Scorer.Score(p.Findings);
        p.Snapshot(action);
    }

    /// <summary>AI review: executive summary, top risks, contextual findings. Best effort.</summary>
    public async Task ReviewAsync(Project p, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_ai.Available)
        {
            p.AddLog($"AI review skipped: {_ai.Error}", "warn");
            return;
        }
        p.Status = ProjectStatus.Reviewing;
        _store.Save(p);
        try
        {
            var ruleFindings = p.Findings.Where(f => f.Source == FindingSource.Rule && f.Status == FindingStatus.Open).ToList();
            p.Analysis = await _ai.Ai!.AnalyzeAsync(p.Model, ruleFindings, progress, ct);

            var existing = p.Findings.Select(f => f.Id).ToHashSet();
            var added = 0;
            foreach (var af in p.Analysis.AdditionalFindings)
            {
                var f = af.ToFinding();
                if (existing.Add(f.Id)) { p.Findings.Add(f); added++; }
            }
            p.Findings = p.Findings.OrderBy(f => f.Status).ThenByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToList();
            p.Score = Scorer.Score(p.Findings);
            p.Snapshot("AI review");
            p.AddLog($"AI review: {p.Analysis.TopRisks.Count} top risks, {added} contextual findings added. Resilience {p.Score.Resilience}, Security {p.Score.Security}.");
        }
        catch (Exception ex) when (ex is AiUnavailableException or AiResponseException)
        {
            _log.LogWarning(ex, "AI review failed for {Id}", p.Id);
            p.AddLog("AI review unavailable: " + ex.Message, "warn");
        }
    }

    public async Task<FixProposal> ProposeFixAsync(Project p, string findingId, CancellationToken ct)
    {
        if (!_ai.Available) throw new AiUnavailableException(_ai.Error ?? "No AI provider configured.");
        var target = p.Findings.FirstOrDefault(f => f.Id == findingId) ?? throw new KeyNotFoundException($"Finding {findingId} not found.");
        var progress = new LogProgress(p);
        var proposal = await _ai.Ai!.ProposeFixAsync(p.Model, target, p.Findings, p.Digest, progress, ct);
        if (!proposal.ResolvesFindingIds.Contains(findingId)) proposal.ResolvesFindingIds.Insert(0, findingId);
        p.Proposals[findingId] = proposal;
        p.AddLog($"Fix proposed for \"{target.Title}\": {proposal.Summary}");
        _store.Save(p);
        return proposal;
    }

    public Project ApplyFix(Project p, string findingId)
    {
        if (!p.Proposals.TryGetValue(findingId, out var proposal))
            throw new KeyNotFoundException("No proposal for this finding yet. Call /fix first.");

        var before = (p.Score.Resilience, p.Score.Security);
        p.Model = ModelPatcher.Apply(p.Model, proposal.Patch);

        foreach (var f in p.Findings.Where(f => f.Source == FindingSource.Ai && proposal.ResolvesFindingIds.Contains(f.Id)))
            f.Status = FindingStatus.Fixed;

        Analyze(p, $"Applied fix: {proposal.Summary}");
        p.AddLog($"Applied fix ({proposal.Patch.Ops.Count} model changes). Resilience {before.Resilience} → {p.Score.Resilience}, Security {before.Security} → {p.Score.Security}.");
        _store.Save(p);
        return p;
    }

    private sealed class LogProgress : IProgress<string>
    {
        private readonly Project _p;
        public LogProgress(Project p) => _p = p;
        public void Report(string value) => _p.AddLog(value);
    }
}
