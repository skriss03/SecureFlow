using System.Text.Json;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Samples;
using SecureFlow.Core.Scoring;
using SecureFlow.Core.Simulation;
using Xunit;

namespace SecureFlow.Core.Tests;

public class RuleTests
{
    private static readonly List<Finding> ShopFast = RuleCatalog.Evaluate(SampleModels.ShopFast());
    private static readonly List<Finding> Healthy = RuleCatalog.Evaluate(SampleModels.Healthy());

    private static IEnumerable<Finding> Of(IEnumerable<Finding> fs, string ruleId) => fs.Where(f => f.RuleId == ruleId);

    [Theory]
    [InlineData("R01", "orders-db")]      // SPOF
    [InlineData("R02", "orders-db")]      // single zone
    [InlineData("R04", "orders-api")]     // no timeout
    [InlineData("R06", "orders-api")]     // no circuit breaker to payments
    [InlineData("R07", "notify-worker")]  // no queue before worker
    [InlineData("R08", "orders-api")]     // no health check
    [InlineData("R09", "orders-db")]      // no backup
    [InlineData("R10", "web")]            // cache without fallback... web has orders-api path, see test below
    [InlineData("R12", "orders-db")]      // shared database
    [InlineData("S02", "web")]            // unencrypted http
    [InlineData("S03", "orders-api")]     // hardcoded secrets
    [InlineData("S07", "gateway")]        // no rate limiting
    public void ShopFast_trips_expected_rules(string ruleId, string componentId)
    {
        var hits = Of(ShopFast, ruleId).ToList();
        if (ruleId == "R10")
        {
            // Storefront calls Orders API (a system of record via API), so R10 should stay quiet for it.
            Assert.DoesNotContain(hits, f => f.ComponentIds.Contains(componentId));
            return;
        }
        Assert.Contains(hits, f => f.ComponentIds.Contains(componentId));
    }

    [Fact]
    public void ShopFast_has_no_monitoring_and_no_identity_provider()
    {
        Assert.Single(Of(ShopFast, "R14"));
        Assert.Single(Of(ShopFast, "S12"));
        Assert.Single(Of(ShopFast, "S08"));
    }

    [Fact]
    public void ShopFast_deep_sync_chain_is_reported()
    {
        // user -> cdn -> gateway -> web -> orders-api -> inventory-api -> orders-db = 6 hops
        var f = Assert.Single(Of(ShopFast, "R11"));
        Assert.Contains("orders-db", f.ComponentIds);
        Assert.Contains("6 synchronous hops", f.Description);
    }

    [Fact]
    public void Healthy_model_only_raises_low_or_info_findings()
    {
        var loud = Healthy.Where(f => f.Severity >= Severity.Medium).ToList();
        Assert.True(loud.Count == 0, "Unexpected findings: " + string.Join("; ", loud.Select(f => $"{f.RuleId} {f.Description}")));
    }

    [Fact]
    public void Every_rule_fires_at_least_once_across_fixtures()
    {
        var fired = ShopFast.Concat(Healthy).Select(f => f.RuleId).ToHashSet();
        var silent = RuleCatalog.All.Select(r => r.Id).Where(id => !fired.Contains(id)).ToList();
        // Rules that need a model shape ShopFast does not have are exercised in dedicated tests below.
        var coveredElsewhere = new[] { "S05", "S10", "S11", "R10" };
        Assert.Empty(silent.Except(coveredElsewhere));
    }

    [Fact]
    public void Public_datastore_and_direct_user_access_are_critical()
    {
        var m = new ArchitectureModel
        {
            Components =
            {
                new() { Id = "user", Name = "User", Type = ComponentType.User },
                new() { Id = "bucket", Name = "Uploads bucket", Type = ComponentType.ObjectStorage, Props = new() { PubliclyExposed = true } },
                new() { Id = "web", Name = "Web", Type = ComponentType.WebApp },
                new() { Id = "cache", Name = "Cache", Type = ComponentType.Cache },
            },
            Flows =
            {
                new() { Id = "f1", From = "user", To = "bucket", Protocol = "https" },
                new() { Id = "f2", From = "user", To = "web", Protocol = "https" },
                new() { Id = "f3", From = "web", To = "cache", Protocol = "tcp" },
            },
        };
        var fs = RuleCatalog.Evaluate(m);
        Assert.Contains(fs, f => f.RuleId == "S05" && f.Severity == Severity.Critical);
        Assert.Contains(fs, f => f.RuleId == "S10" && f.Severity == Severity.Critical);
        Assert.Contains(fs, f => f.RuleId == "S11" && f.ComponentIds.Contains("web"));
        Assert.Contains(fs, f => f.RuleId == "R10" && f.ComponentIds.Contains("web"));
    }

    [Fact]
    public void Finding_ids_are_stable_across_runs()
    {
        var a = RuleCatalog.Evaluate(SampleModels.ShopFast()).Select(f => f.Id).ToList();
        var b = RuleCatalog.Evaluate(SampleModels.ShopFast()).Select(f => f.Id).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Scores_reflect_findings_and_healthy_scores_high()
    {
        var bad = Scorer.Score(ShopFast);
        var good = Scorer.Score(Healthy);
        Assert.True(bad.Resilience < 50, $"ShopFast resilience {bad.Resilience}");
        Assert.True(bad.Security < 50, $"ShopFast security {bad.Security}");
        Assert.True(good.Resilience >= 90, $"Healthy resilience {good.Resilience}");
        Assert.True(good.Security >= 90, $"Healthy security {good.Security}");
        Assert.True(bad.ComponentHeat["orders-db"] > 0.5);
    }

    [Fact]
    public void Applying_a_fix_patch_removes_the_finding_and_raises_the_score()
    {
        var model = SampleModels.ShopFast();
        var before = RuleCatalog.Evaluate(model);
        var target = before.First(f => f.RuleId == "R06");

        var patch = new ModelPatch
        {
            Ops =
            {
                new PatchOp { Kind = "set-flow-prop", Id = "f10", Property = "circuitBreaker", Value = JsonSerializer.SerializeToElement(true) },
                new PatchOp { Kind = "set-flow-prop", Id = "f10", Property = "timeoutMs", Value = JsonSerializer.SerializeToElement(3000) },
            },
        };
        var patched = ModelPatcher.Apply(model, patch);
        var after = RuleCatalog.Evaluate(patched);

        Assert.DoesNotContain(after, f => f.Id == target.Id);
        Assert.True(Scorer.Score(after).Resilience > Scorer.Score(before).Resilience);
    }

    [Fact]
    public void Blast_radius_of_orders_db_reaches_the_storefront()
    {
        var r = BlastRadius.Simulate(SampleModels.ShopFast(), "orders-db");
        var by = r.Impacts.ToDictionary(i => i.ComponentId, i => i.Level);
        Assert.Equal(ImpactLevel.Killed, by["orders-db"]);
        Assert.Equal(ImpactLevel.Down, by["orders-api"]);
        Assert.Equal(ImpactLevel.Down, by["inventory-api"]);
        Assert.Equal(ImpactLevel.Down, by["web"]);
        Assert.Equal(ImpactLevel.Down, by["gateway"]);
        Assert.Equal(ImpactLevel.Ok, by["notify-worker"]);
        Assert.Equal(ImpactLevel.Ok, by["cache"]);
        Assert.Contains("User-facing outage", r.Summary);
    }

    [Fact]
    public void Blast_radius_respects_queues_and_circuit_breakers()
    {
        var r = BlastRadius.Simulate(SampleModels.Healthy(), "db");
        var by = r.Impacts.ToDictionary(i => i.ComponentId, i => i.Level);
        Assert.Equal(ImpactLevel.Down, by["api"]);     // sync, no CB in the healthy model either
        Assert.Equal(ImpactLevel.Down, by["gw"]);      // gateway -> api is sync: cascades
        Assert.Equal(ImpactLevel.Ok, by["worker"]);    // worker does not touch the db
        Assert.Equal(ImpactLevel.Ok, by["q"]);

        var r2 = BlastRadius.Simulate(SampleModels.Healthy(), "worker");
        var by2 = r2.Impacts.ToDictionary(i => i.ComponentId, i => i.Level);
        Assert.Equal(ImpactLevel.Degraded, by2["q"]);  // queue -> worker is async: buffered
        Assert.Equal(ImpactLevel.Ok, by2["api"]);      // degraded does not propagate
    }
}
