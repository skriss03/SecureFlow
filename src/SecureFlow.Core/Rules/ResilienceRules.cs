using SecureFlow.Core.Model;
using static SecureFlow.Core.Rules.ModelGraph;

namespace SecureFlow.Core.Rules;

public sealed class SinglePointOfFailure : Rule
{
    public override string Id => "R01";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "SPOF";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Single point of failure";
    public override string Rationale => "A component with one instance takes every dependent down with it during a crash, a deploy, or a host failure. Availability is capped by the weakest single instance.";
    public override string Mitigation => "Run at least two instances behind a load balancer or use a managed, zone-redundant tier. Make dependents tolerate a short outage with retries and a queue where writes can wait.";
    public override string? FixHint => "set-component-prop replicas >= 2 (and zones >= 2 for data stores)";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsInfrastructure(c)) continue;
            if (c.Props.Managed == true && c.Props.Replicas is null) continue; // assume the provider handles it
            var dependents = g.Inbound(c.Id).Select(f => f.From).Where(id => !IsActor(g.ById[id])).Distinct().ToList();
            if (dependents.Count == 0 && !IsDataStore(c)) continue;
            if ((c.Props.Replicas ?? 1) > 1) continue;

            var sev = c.Type is ComponentType.Database or ComponentType.Queue or ComponentType.Identity or ComponentType.Gateway or ComponentType.LoadBalancer
                ? Severity.Critical : Severity.High;
            var who = dependents.Count == 0 ? "" : $" {dependents.Count} component(s) depend on it: {g.Names(dependents)}.";
            yield return new RuleMatch(
                $"{c.Name} runs as a single instance.{who}",
                Ids(c.Id), None, Conf(c.Props.Replicas), sev);
        }
    }
}

public sealed class SingleZoneDataStore : Rule
{
    public override string Id => "R02";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Zone redundancy";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Data store confined to one availability zone";
    public override string Rationale => "Zone-level incidents (power, network, cooling) are the most common cloud outage class. A single-zone data store turns one of them into a full outage with possible data loss.";
    public override string Mitigation => "Enable zone-redundant storage or synchronous replicas across at least two zones.";
    public override string? FixHint => "set-component-prop zones = 2 or 3";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsDataStore(c)) continue;
            if ((c.Props.Zones ?? 1) > 1) continue;
            var sev = c.Type is ComponentType.Database or ComponentType.Queue ? Severity.High : Severity.Medium;
            yield return new RuleMatch($"{c.Name} is not zone-redundant.", Ids(c.Id), None, Conf(c.Props.Zones), sev);
        }
    }
}

public sealed class NoDisasterRecoveryRegion : Rule
{
    public override string Id => "R03";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Disaster recovery";
    public override Severity DefaultSeverity => Severity.Low;
    public override string Title => "No secondary region for disaster recovery";
    public override string Rationale => "Regional outages are rare but long. Without a replica or restorable copy in another region, recovery time is measured in days.";
    public override string Mitigation => "Geo-replicate the data store or ship backups to a second region and document the failover runbook.";
    public override string? FixHint => "set-component-prop regions = 2";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type is not (ComponentType.Database or ComponentType.ObjectStorage)) continue;
            if ((c.Props.Regions ?? 1) > 1) continue;
            yield return new RuleMatch($"{c.Name} exists in a single region.", Ids(c.Id), None, Conf(c.Props.Regions));
        }
    }
}

public sealed class SyncCallWithoutTimeout : Rule
{
    public override string Id => "R04";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Timeout";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Synchronous call without a timeout";
    public override string Rationale => "A slow dependency holds threads and connections until the caller runs out. Without a timeout, a partial slowdown becomes a full outage that spreads upstream.";
    public override string Mitigation => "Set an explicit client timeout below the caller's own budget and fail fast. Pair it with a retry policy and a circuit breaker.";
    public override string? FixHint => "set-flow-prop timeoutMs";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            var from = g.From(f); var to = g.To(f);
            if (!IsSync(f) || IsActor(from) || to.Type == ComponentType.User) continue;
            if (f.TimeoutMs is > 0) continue;
            var sev = to.Type is ComponentType.External or ComponentType.Database ? Severity.High : Severity.Medium;
            var conf = f.TimeoutMs is null ? Confidence.Medium : Confidence.High;
            yield return new RuleMatch($"{from.Name} calls {to.Name} synchronously with no timeout.", Ids(from.Id, to.Id), Ids(f.Id), conf, sev);
        }
    }
}

public sealed class SyncCallWithoutRetry : Rule
{
    public override string Id => "R05";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Retry";
    public override Severity DefaultSeverity => Severity.Low;
    public override string Title => "Synchronous call without retry policy";
    public override string Rationale => "Transient faults (connection resets, throttling, brief failovers) are normal in distributed systems. Without a bounded retry with backoff they surface as user-visible errors.";
    public override string Mitigation => "Add a bounded retry with exponential backoff and jitter for idempotent operations. Never retry without a timeout.";
    public override string? FixHint => "set-flow-prop retries";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            var from = g.From(f); var to = g.To(f);
            if (!IsSync(f) || IsActor(from) || to.Type == ComponentType.User) continue;
            if ((f.Retries ?? 0) > 0) continue;
            yield return new RuleMatch($"{from.Name} → {to.Name} has no retry policy.", Ids(from.Id, to.Id), Ids(f.Id), f.Retries is null ? Confidence.Medium : Confidence.High);
        }
    }
}

public sealed class ExternalDependencyWithoutCircuitBreaker : Rule
{
    public override string Id => "R06";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Circuit breaker";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "External dependency without a circuit breaker";
    public override string Rationale => "Third-party outages are outside your control and often long. Without a circuit breaker every request keeps waiting on the dead dependency, exhausting the caller.";
    public override string Mitigation => "Wrap the call in a circuit breaker that opens after repeated failures and returns a fallback (cached value, degraded feature, queued retry).";
    public override string? FixHint => "set-flow-prop circuitBreaker = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            var from = g.From(f); var to = g.To(f);
            if (to.Type != ComponentType.External || !IsSync(f) || IsActor(from)) continue;
            if (f.CircuitBreaker == true) continue;
            yield return new RuleMatch($"{from.Name} depends on external {to.Name} with no circuit breaker.", Ids(from.Id, to.Id), Ids(f.Id), Conf(f.CircuitBreaker));
        }
    }
}

public sealed class NoQueueBeforeWorker : Rule
{
    public override string Id => "R07";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Load leveling";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Background work invoked synchronously";
    public override string Rationale => "Calling a worker directly couples request latency to slow work and lets a traffic burst overwhelm it. A queue absorbs bursts and lets the worker fail without failing the request.";
    public override string Mitigation => "Put a queue or topic between the caller and the worker. Make the caller enqueue and return; make the worker idempotent.";
    public override string? FixHint => "add-component queue; add-flow caller→queue (isSync=false); add-flow queue→worker; remove-flow caller→worker";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type != ComponentType.Worker) continue;
            var inbound = g.Inbound(c.Id);
            if (inbound.Count == 0) continue;
            if (inbound.Any(f => g.From(f).Type == ComponentType.Queue || !IsSync(f))) continue;
            var callers = inbound.Select(f => f.From).Distinct().ToList();
            yield return new RuleMatch($"{g.Names(callers)} → {c.Name} is a synchronous call into a worker; no queue in between.",
                callers.Append(c.Id).ToList(), inbound.Select(f => f.Id).ToList());
        }
    }
}

public sealed class NoHealthCheck : Rule
{
    public override string Id => "R08";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Health check";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "No health check endpoint";
    public override string Rationale => "Without a health probe the platform cannot detect a hung instance, restart it, or pull it out of the load balancer. Failures stay invisible until users report them.";
    public override string Mitigation => "Expose liveness and readiness endpoints that verify critical dependencies, and wire them to the orchestrator and load balancer.";
    public override string? FixHint => "set-component-prop hasHealthCheck = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsService(c)) continue;
            if (c.Props.HasHealthCheck == true) continue;
            yield return new RuleMatch($"{c.Name} has no health check.", Ids(c.Id), None, Conf(c.Props.HasHealthCheck));
        }
    }
}

public sealed class NoBackup : Rule
{
    public override string Id => "R09";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Backup";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Data store without backups";
    public override string Rationale => "Replication protects against hardware failure, not against bad deploys, ransomware, or a wrong DELETE. Without point-in-time backups those are unrecoverable.";
    public override string Mitigation => "Enable automated backups with point-in-time restore, test a restore, and keep a copy outside the primary account or region.";
    public override string? FixHint => "set-component-prop hasBackup = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type is not (ComponentType.Database or ComponentType.ObjectStorage)) continue;
            if (c.Props.HasBackup == true) continue;
            var sev = c.Type == ComponentType.Database ? Severity.High : Severity.Medium;
            yield return new RuleMatch($"{c.Name} has no backup configured.", Ids(c.Id), None, Conf(c.Props.HasBackup), sev);
        }
    }
}

public sealed class CacheWithoutFallback : Rule
{
    public override string Id => "R10";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Cache dependency";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Cache used as the only data path";
    public override string Rationale => "A cache that has no system of record behind it is a database with no durability. An eviction, restart, or flush becomes data loss or an outage.";
    public override string Mitigation => "Treat the cache as an optimization: read through to the source of truth on a miss and tolerate cache unavailability.";
    public override string? FixHint => "add-flow caller→database";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var cache in g.Model.Components)
        {
            if (cache.Type != ComponentType.Cache) continue;
            foreach (var f in g.Inbound(cache.Id))
            {
                var caller = g.From(f);
                if (IsActor(caller)) continue;
                var hasSource = g.Outbound(caller.Id).Any(o => g.To(o).Type is ComponentType.Database or ComponentType.Api);
                if (hasSource) continue;
                yield return new RuleMatch($"{caller.Name} reads {cache.Name} but has no path to a system of record.", Ids(caller.Id, cache.Id), Ids(f.Id));
            }
        }
    }
}

public sealed class DeepSyncChain : Rule
{
    public override string Id => "R11";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Cascading failure";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Deep synchronous call chain";
    public override string Rationale => "Each synchronous hop multiplies latency and failure probability. A chain of four or more services means the slowest or flakiest one dictates the user experience and any failure cascades to the top.";
    public override string Mitigation => "Break the chain with asynchronous messaging, cache intermediate results, or collapse services. Add timeouts and bulkheads at every hop.";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        var entries = g.Model.Components.Where(c => c.Type == ComponentType.User || g.Inbound(c.Id).Count == 0).ToList();
        if (entries.Count == 0) entries = g.Model.Components.ToList();
        var longest = entries.Select(e => g.LongestSyncChainFrom(e.Id)).OrderByDescending(p => p.Count).FirstOrDefault();
        if (longest is null) yield break;
        var hops = longest.Count - 1;
        if (hops < 4) yield break;
        var flowIds = new List<string>();
        for (var i = 0; i < longest.Count - 1; i++)
        {
            var f = g.Outbound(longest[i]).FirstOrDefault(x => x.To == longest[i + 1] && IsSync(x));
            if (f is not null) flowIds.Add(f.Id);
        }
        yield return new RuleMatch($"{hops} synchronous hops: {string.Join(" → ", longest.Select(g.NameOf))}.", longest, flowIds);
    }
}

public sealed class SharedDatabase : Rule
{
    public override string Id => "R12";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Coupling";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Database shared by multiple services";
    public override string Rationale => "A shared database couples deployments, lets one noisy service starve the others, and makes schema changes a coordination problem. It is also a single blast radius.";
    public override string Mitigation => "Give each service its own schema or database, or at least separate connection pools and resource limits. Expose data via APIs or events instead of shared tables.";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var db in g.Model.Components)
        {
            if (db.Type != ComponentType.Database) continue;
            var callers = g.Inbound(db.Id).Select(f => f.From).Where(id => IsService(g.ById[id])).Distinct().ToList();
            if (callers.Count < 2) continue;
            yield return new RuleMatch($"{db.Name} is written by {callers.Count} services: {g.Names(callers)}.", callers.Append(db.Id).ToList(), g.Inbound(db.Id).Select(f => f.Id).ToList());
        }
    }
}

public sealed class NoAutoscaleOnPublicTier : Rule
{
    public override string Id => "R13";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Capacity";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Public-facing tier does not autoscale";
    public override string Rationale => "Public traffic is spiky and adversarial. A fixed-size tier either wastes money or falls over under a campaign, a crawler, or a flash crowd.";
    public override string Mitigation => "Enable horizontal autoscaling on request rate or CPU with sensible min/max, and load test the scale-out path.";
    public override string? FixHint => "set-component-prop autoscale = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsService(c) || !g.IsPublic(c)) continue;
            if (c.Props.Autoscale == true) continue;
            yield return new RuleMatch($"{c.Name} is public-facing without autoscaling.", Ids(c.Id), None, Conf(c.Props.Autoscale));
        }
    }
}

public sealed class NoObservability : Rule
{
    public override string Id => "R14";
    public override FindingCategory Category => FindingCategory.Resilience;
    public override string Tag => "Observability";
    public override Severity DefaultSeverity => Severity.Low;
    public override string Title => "No monitoring or logging component modeled";
    public override string Rationale => "Mean time to recovery is dominated by mean time to notice. Without centralized logs, metrics and alerts, outages are discovered by customers.";
    public override string Mitigation => "Add centralized logging, metrics and tracing with alerting on error rate, latency and saturation for every service.";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        if (g.Model.Components.Any(c => c.Type == ComponentType.Monitoring)) yield break;
        if (g.Model.Components.Count == 0) yield break;
        yield return new RuleMatch("The architecture has no monitoring, logging or alerting component.", None, None, Confidence.Medium);
    }
}
