using System.Text.Json.Serialization;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;

namespace SecureFlow.Core.Simulation;

[JsonConverter(typeof(JsonStringEnumConverter<ImpactLevel>))]
public enum ImpactLevel { Killed, Down, Degraded, Ok }

public sealed class ComponentImpact
{
    public string ComponentId { get; set; } = "";
    public string Name { get; set; } = "";
    public ImpactLevel Level { get; set; }
    /// <summary>Human explanation, e.g. "calls orders-db synchronously with no circuit breaker".</summary>
    public string Reason { get; set; } = "";
    /// <summary>Path of component ids from this component down to the killed one.</summary>
    public List<string> Via { get; set; } = new();
}

public sealed class BlastRadiusResult
{
    public string KilledComponentId { get; set; } = "";
    public List<ComponentImpact> Impacts { get; set; } = new();
    public int DownCount => Impacts.Count(i => i.Level == ImpactLevel.Down);
    public int DegradedCount => Impacts.Count(i => i.Level == ImpactLevel.Degraded);
    public int TotalCount => Impacts.Count;
    /// <summary>Share of non-actor components that are down or degraded, 0..1.</summary>
    public double BlastRatio { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// Deterministic failure propagation. No LLM involved: click a node, get an answer in microseconds.
///
/// Rules of propagation, walking callers backwards from the failed component:
///   sync call, no circuit breaker  -> caller is Down (it blocks and exhausts)
///   sync call, circuit breaker     -> caller is Degraded (fails fast, serves fallback)
///   async call / via queue         -> caller is Degraded (work is buffered, not lost)
///   caller has another Ok replica path -> still Down for simplicity; the model is per logical component
/// Down propagates transitively; Degraded does not.
/// </summary>
public static class BlastRadius
{
    public static BlastRadiusResult Simulate(ArchitectureModel model, string killId)
    {
        var g = new ModelGraph(model);
        if (!g.ById.ContainsKey(killId))
            throw new ArgumentException($"Unknown component '{killId}'", nameof(killId));

        var result = new BlastRadiusResult { KilledComponentId = killId };
        var state = new Dictionary<string, ComponentImpact>();
        state[killId] = new ComponentImpact { ComponentId = killId, Name = g.NameOf(killId), Level = ImpactLevel.Killed, Reason = "Simulated failure.", Via = new List<string> { killId } };

        var queue = new Queue<string>();
        queue.Enqueue(killId);

        while (queue.Count > 0)
        {
            var failed = queue.Dequeue();
            var failedImpact = state[failed];
            if (failedImpact.Level is not (ImpactLevel.Killed or ImpactLevel.Down)) continue;

            foreach (var f in g.Inbound(failed))
            {
                var caller = g.From(f);
                if (ModelGraph.IsActor(caller)) continue;

                var (level, reason) = Classify(f, g.NameOf(failed), g.To(f));
                var via = new List<string> { caller.Id };
                via.AddRange(failedImpact.Via);

                if (state.TryGetValue(caller.Id, out var existing))
                {
                    if (existing.Level <= level) continue; // already as bad or worse
                    existing.Level = level; existing.Reason = reason; existing.Via = via;
                }
                else
                {
                    state[caller.Id] = new ComponentImpact { ComponentId = caller.Id, Name = caller.Name, Level = level, Reason = reason, Via = via };
                }
                if (level == ImpactLevel.Down) queue.Enqueue(caller.Id);
            }
        }

        foreach (var c in model.Components)
        {
            if (!state.ContainsKey(c.Id))
                state[c.Id] = new ComponentImpact { ComponentId = c.Id, Name = c.Name, Level = ImpactLevel.Ok, Reason = "No dependency on the failed component.", Via = new() };
        }

        result.Impacts = state.Values.OrderBy(i => i.Level).ThenBy(i => i.Name).ToList();
        var infra = model.Components.Count(c => !ModelGraph.IsActor(c));
        var hit = result.Impacts.Count(i => i.Level is ImpactLevel.Down or ImpactLevel.Degraded or ImpactLevel.Killed);
        result.BlastRatio = infra == 0 ? 0 : Math.Round((double)hit / infra, 3);

        var users = model.Components.Where(c => c.Type == ComponentType.User).Select(c => c.Id).ToHashSet();
        var userFacingDown = result.Impacts.Where(i => i.Level == ImpactLevel.Down && g.Inbound(i.ComponentId).Any(f => users.Contains(f.From))).Select(i => i.Name).ToList();
        result.Summary = userFacingDown.Count > 0
            ? $"Losing {g.NameOf(killId)} takes down {result.DownCount} component(s) and degrades {result.DegradedCount}. User-facing outage via {string.Join(", ", userFacingDown)}."
            : $"Losing {g.NameOf(killId)} takes down {result.DownCount} component(s) and degrades {result.DegradedCount}. No user-facing component is fully down.";
        return result;
    }

    private static (ImpactLevel, string) Classify(Flow f, string failedName, Component failedComponent)
    {
        if (failedComponent.Type == ComponentType.Queue)
            return (ImpactLevel.Degraded, $"Publishes to {failedName}; messages cannot be enqueued until it recovers.");
        if (!ModelGraph.IsSync(f))
            return (ImpactLevel.Degraded, $"Sends work to {failedName} asynchronously; work is delayed, not lost.");
        if (f.CircuitBreaker == true)
            return (ImpactLevel.Degraded, $"Calls {failedName} behind a circuit breaker; fails fast and serves a fallback.");
        var timeoutNote = f.TimeoutMs is > 0 ? "" : " with no timeout";
        return (ImpactLevel.Down, $"Calls {failedName} synchronously{timeoutNote} and no circuit breaker; requests pile up until the caller exhausts itself.");
    }
}
