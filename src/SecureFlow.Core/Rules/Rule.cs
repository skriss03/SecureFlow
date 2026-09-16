using System.Security.Cryptography;
using System.Text;
using SecureFlow.Core.Model;

namespace SecureFlow.Core.Rules;

/// <summary>One match of a rule against the model. Description names the components involved.</summary>
public sealed record RuleMatch(
    string Description,
    IReadOnlyList<string> ComponentIds,
    IReadOnlyList<string> FlowIds,
    Confidence Confidence = Confidence.High,
    Severity? Severity = null);

/// <summary>
/// A deterministic rule. Metadata lives on the class so the catalog is self-describing
/// (exposed via GET /api/rules); the predicate lives in <see cref="Evaluate"/>.
/// </summary>
public abstract class Rule
{
    public abstract string Id { get; }
    public abstract FindingCategory Category { get; }
    /// <summary>Resilience class or STRIDE name.</summary>
    public abstract string Tag { get; }
    public abstract Severity DefaultSeverity { get; }
    public abstract string Title { get; }
    public abstract string Rationale { get; }
    public abstract string Mitigation { get; }
    public virtual string? FixHint => null;

    public abstract IEnumerable<RuleMatch> Evaluate(ModelGraph g);

    public IEnumerable<Finding> Run(ModelGraph g)
    {
        foreach (var m in Evaluate(g))
        {
            var evidence = m.ComponentIds.SelectMany(id => g.ById.TryGetValue(id, out var c) ? c.Evidence : Enumerable.Empty<Evidence>())
                .Concat(m.FlowIds.SelectMany(id => g.Model.FindFlow(id)?.Evidence ?? new List<Evidence>()))
                .Take(6)
                .ToList();

            yield return new Finding
            {
                Id = StableId(Id, m.ComponentIds, m.FlowIds),
                RuleId = Id,
                Category = Category,
                Tag = Tag,
                Severity = m.Severity ?? DefaultSeverity,
                Confidence = m.Confidence,
                Source = FindingSource.Rule,
                Title = Title,
                Description = m.Description,
                Rationale = Rationale,
                Mitigation = Mitigation,
                ComponentIds = m.ComponentIds.ToList(),
                FlowIds = m.FlowIds.ToList(),
                Evidence = evidence,
                FixHint = FixHint,
            };
        }
    }

    /// <summary>Stable across re-runs so a fixed finding visibly disappears and an open one keeps its id.</summary>
    public static string StableId(string ruleId, IEnumerable<string> componentIds, IEnumerable<string> flowIds)
    {
        var key = $"{ruleId}|{string.Join(",", componentIds.Order())}|{string.Join(",", flowIds.Order())}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    protected static Confidence Conf(bool? v) => v is null ? Confidence.Medium : Confidence.High;
    protected static Confidence Conf(int? v) => v is null ? Confidence.Medium : Confidence.High;
    protected static string[] Ids(params string[] ids) => ids;
    protected static readonly string[] None = Array.Empty<string>();
}

public sealed class RuleInfo
{
    public string Id { get; init; } = "";
    public FindingCategory Category { get; init; }
    public string Tag { get; init; } = "";
    public Severity DefaultSeverity { get; init; }
    public string Title { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string Mitigation { get; init; } = "";
}

public static class RuleCatalog
{
    public static IReadOnlyList<Rule> All { get; } = new Rule[]
    {
        // Resilience
        new SinglePointOfFailure(),
        new SingleZoneDataStore(),
        new NoDisasterRecoveryRegion(),
        new SyncCallWithoutTimeout(),
        new SyncCallWithoutRetry(),
        new ExternalDependencyWithoutCircuitBreaker(),
        new NoQueueBeforeWorker(),
        new NoHealthCheck(),
        new NoBackup(),
        new CacheWithoutFallback(),
        new DeepSyncChain(),
        new SharedDatabase(),
        new NoAutoscaleOnPublicTier(),
        new NoObservability(),
        // Security (STRIDE)
        new UnauthenticatedCrossBoundaryFlow(),
        new UnencryptedFlow(),
        new HardcodedSecrets(),
        new NoAuditLogging(),
        new PubliclyExposedDataStore(),
        new NoInputValidationAtEdge(),
        new NoRateLimiting(),
        new NoSecretsManager(),
        new NoEncryptionAtRest(),
        new DirectDataAccessFromUntrusted(),
        new NoEdgeProtection(),
        new NoIdentityProvider(),
    };

    public static IReadOnlyList<RuleInfo> Describe() => All.Select(r => new RuleInfo
    {
        Id = r.Id, Category = r.Category, Tag = r.Tag, DefaultSeverity = r.DefaultSeverity,
        Title = r.Title, Rationale = r.Rationale, Mitigation = r.Mitigation,
    }).ToList();

    public static List<Finding> Evaluate(ArchitectureModel model)
    {
        var g = new ModelGraph(model);
        return All.SelectMany(r => r.Run(g))
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => f.RuleId)
            .ToList();
    }
}
