using System.Text.Json;
using SecureFlow.Ai;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Ingest;
using Xunit;

namespace SecureFlow.Core.Tests;

public class IngestAndAiTests
{
    private static string SamplePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SecureFlow.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? ".", "samples", name);
    }

    [Fact]
    public void Drawio_sample_parses_into_a_full_model_without_ai()
    {
        var model = DrawioIngest.Parse(File.ReadAllText(SamplePath("shopfast.drawio")), "shopfast.drawio");

        Assert.Equal(11, model.Components.Count);
        Assert.Equal(12, model.Flows.Count);
        Assert.Equal(4, model.TrustBoundaries.Count);

        var db = Assert.Single(model.Components, c => c.Id.StartsWith("orders-db"));
        Assert.Equal(ComponentType.Database, db.Type);
        var gateway = Assert.Single(model.Components, c => c.Id.StartsWith("api-gateway"));
        Assert.Equal(ComponentType.Gateway, gateway.Type);
        Assert.Equal(2, gateway.Props.Replicas);
        var worker = Assert.Single(model.Components, c => c.Type == ComponentType.Worker);
        Assert.Equal(1, worker.Props.Replicas);
        Assert.Equal(2, model.Components.Count(c => c.Type == ComponentType.External));
        Assert.Single(model.Components, c => c.Type == ComponentType.User);

        var gwToWeb = Assert.Single(model.Flows, f => f.From == gateway.Id && f.To.StartsWith("storefront"));
        Assert.Equal("http", gwToWeb.Protocol);
        Assert.Equal(30000, gwToWeb.TimeoutMs);
        var userToGw = Assert.Single(model.Flows, f => f.From.StartsWith("shopper") && f.To == gateway.Id);
        Assert.Equal("jwt", userToGw.Auth);

        var privateNet = Assert.Single(model.TrustBoundaries, b => b.Name.StartsWith("Private"));
        Assert.Contains(db.Id, privateNet.ComponentIds);

        // The deterministic path should light up the same headline rules as the hand-built sample.
        var findings = RuleCatalog.Evaluate(model);
        Assert.Contains(findings, f => f.RuleId == "R01" && f.ComponentIds.Contains(db.Id));
        Assert.Contains(findings, f => f.RuleId == "R07");
        Assert.Contains(findings, f => f.RuleId == "S02");
    }

    [Fact]
    public void AiJson_parse_tolerates_code_fences_and_prose()
    {
        var text = "Here is the model:\n```json\n{\"name\":\"X\",\"components\":[],\"flows\":[],\"trustBoundaries\":[],\"assumptions\":[]}\n```\nDone.";
        var m = AiJson.Parse<ArchitectureModel>(text);
        Assert.Equal("X", m.Name);
    }

    [Fact]
    public void Ai_patch_ops_coerce_string_values_and_parse_object_json()
    {
        var fix = new AiFixResponse
        {
            Patch = new AiPatch
            {
                Ops =
                {
                    new AiPatchOp { Kind = "set-flow-prop", Id = "f10", Property = "circuitBreaker", Value = JsonSerializer.SerializeToElement("true") },
                    new AiPatchOp { Kind = "set-flow-prop", Id = "f10", Property = "timeoutMs", Value = JsonSerializer.SerializeToElement("3000") },
                    new AiPatchOp { Kind = "add-component", ObjectJson = "{\"id\":\"orders-queue\",\"name\":\"Orders Queue\",\"type\":\"Queue\",\"props\":{\"managed\":true}}" },
                    new AiPatchOp { Kind = "add-flow", ObjectJson = "{\"id\":\"f13\",\"from\":\"orders-api\",\"to\":\"orders-queue\",\"isSync\":false}" },
                    new AiPatchOp { Kind = "add-flow", ObjectJson = "{\"id\":\"f14\",\"from\":\"orders-queue\",\"to\":\"notify-worker\",\"isSync\":false}" },
                    new AiPatchOp { Kind = "remove-flow", Id = "f11" },
                },
            },
        };
        var model = Core.Samples.SampleModels.ShopFast();
        var patched = ModelPatcher.Apply(model, fix.ToProposal("x").Patch);

        var f10 = patched.FindFlow("f10")!;
        Assert.True(f10.CircuitBreaker);
        Assert.Equal(3000, f10.TimeoutMs);
        Assert.NotNull(patched.FindComponent("orders-queue"));
        Assert.Null(patched.FindFlow("f11"));

        var findings = RuleCatalog.Evaluate(patched);
        Assert.DoesNotContain(findings, f => f.RuleId == "R07"); // worker now fed by a queue
        Assert.DoesNotContain(findings, f => f.RuleId == "R06" && f.FlowIds.Contains("f10"));
    }

    [Fact]
    public void Ai_findings_get_stable_ids()
    {
        var a = new AiFinding { Title = "Dual write", ComponentIds = { "orders-api", "orders-db" } }.ToFinding();
        var b = new AiFinding { Title = "Dual write", ComponentIds = { "orders-db", "orders-api" } }.ToFinding();
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(FindingSource.Ai, a.Source);
    }
}
