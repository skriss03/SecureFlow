using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFlow.Ai;
using SecureFlow.Api.Projects;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Samples;
using Xunit;

namespace SecureFlow.Core.Tests;

/// <summary>
/// Covers the API layer's stateful bits: the findings merge in <see cref="ProjectPipeline.Analyze"/>
/// and the thread safety of a <see cref="Project"/> that a background pipeline mutates while
/// request threads serialize it.
/// </summary>
public class ProjectPipelineTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "secureflow-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private ProjectPipeline NewPipeline()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDir"] = _dataDir })
            .Build();
        var store = new ProjectStore(config, new TestEnv(), NullLogger<ProjectStore>.Instance);
        var ai = new AiRegistry { Error = "no provider in tests" };
        return new ProjectPipeline(store, ai, NullLogger<ProjectPipeline>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Retired_findings_appear_exactly_once_after_a_fix_is_applied()
    {
        var pipeline = NewPipeline();
        var project = new Project { Name = "ShopFast", Model = SampleModels.ShopFast() };
        pipeline.Analyze(project, "Initial");

        var target = project.Findings.First(f => f.RuleId == "R06");
        Assert.Equal(FindingStatus.Open, target.Status);

        // Fixing the circuit breaker on f10 stops R06 (and R04) firing for that flow.
        project.Model = ModelPatcher.Apply(project.Model, new ModelPatch
        {
            Ops =
            {
                new PatchOp { Kind = "set-flow-prop", Id = "f10", Property = "circuitBreaker", Value = JsonSerializer.SerializeToElement(true) },
                new PatchOp { Kind = "set-flow-prop", Id = "f10", Property = "timeoutMs", Value = JsonSerializer.SerializeToElement(3000) },
            },
        });
        pipeline.Analyze(project, "Applied fix");

        var duplicates = project.Findings.GroupBy(f => f.Id).Where(g => g.Count() > 1).Select(g => $"{g.Key} x{g.Count()}").ToList();
        Assert.True(duplicates.Count == 0, "Duplicated findings: " + string.Join(", ", duplicates));

        var retired = Assert.Single(project.Findings, f => f.Id == target.Id);
        Assert.Equal(FindingStatus.Fixed, retired.Status);
    }

    [Fact]
    public void Repeated_analysis_does_not_accumulate_duplicates()
    {
        var pipeline = NewPipeline();
        var project = new Project { Model = SampleModels.ShopFast() };

        pipeline.Analyze(project, "first");
        var afterFirst = project.Findings.Count;

        // A finding retired in round one must not be re-added in rounds two and three.
        project.Model = ModelPatcher.Apply(project.Model, new ModelPatch
        {
            Ops = { new PatchOp { Kind = "set-component-prop", Id = "orders-db", Property = "hasBackup", Value = JsonSerializer.SerializeToElement(true) } },
        });
        pipeline.Analyze(project, "second");
        var afterSecond = project.Findings.Count;
        pipeline.Analyze(project, "third");

        Assert.Equal(afterSecond, project.Findings.Count);
        Assert.Equal(afterFirst, project.Findings.Count); // one finding moved from Open to Fixed, none added
        Assert.Empty(project.Findings.GroupBy(f => f.Id).Where(g => g.Count() > 1));
        Assert.Contains(project.Findings, f => f.RuleId == "R09" && f.Status == FindingStatus.Fixed);
    }

    [Fact]
    public void Accepted_findings_keep_their_status_across_analysis()
    {
        var pipeline = NewPipeline();
        var project = new Project { Model = SampleModels.ShopFast() };
        pipeline.Analyze(project, "first");

        var accepted = project.Findings.First(f => f.RuleId == "R03");
        accepted.Status = FindingStatus.Accepted;

        pipeline.Analyze(project, "second");
        Assert.Equal(FindingStatus.Accepted, Assert.Single(project.Findings, f => f.Id == accepted.Id).Status);
    }

    [Fact]
    public async Task Serializing_a_project_while_the_pipeline_writes_does_not_throw()
    {
        // The pipeline runs on a background thread while GET /api/projects/{id} and ProjectStore.Save
        // serialize the same live object on request threads. A plain List<T>.Add during that
        // enumeration throws "Collection was modified". Several short trials with an explicit start
        // gate keep the two threads overlapping, so the race is hit reliably rather than by luck.
        var json = new JsonSerializerOptions(AiJson.Options);
        Exception? failure = null;

        for (var trial = 0; trial < 25 && failure is null; trial++)
        {
            var project = new Project { Model = SampleModels.ShopFast() };
            using var start = new ManualResetEventSlim(false);

            var writer = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 2000; i++)
                {
                    project.AddLog($"step {i}");
                    if (i % 20 == 0) project.Snapshot($"round {i}");
                    if (i % 50 == 0) project.SetProposal($"finding-{i}", new FixProposal { Summary = "s" });
                }
            });

            start.Set();
            while (!writer.IsCompleted && failure is null)
            {
                try
                {
                    // Enumerating these collections is exactly what the serializer does to them,
                    // and it is the step that throws when the pipeline appends at the same time.
                    foreach (var e in project.Log) _ = e.Message;
                    foreach (var h in project.History) _ = h.Action;
                    foreach (var kv in project.Proposals) _ = kv.Key;
                }
                catch (Exception ex) { failure = ex; }
            }
            await writer;
            if (failure is null) JsonSerializer.Serialize(project, json);
        }

        Assert.True(failure is null, "Serialization threw while the pipeline was writing: " + failure?.Message);
    }

    [Fact]
    public void Log_reads_are_consistent_while_appending()
    {
        var project = new Project();
        for (var i = 0; i < 5; i++) project.AddLog($"m{i}");

        Assert.Equal(5, project.LogSince(0).Count);
        Assert.Equal(2, project.LogSince(3).Count);
        Assert.Empty(project.LogSince(5));
        Assert.Empty(project.LogSince(99));
        Assert.Equal("m3", project.LogSince(3)[0].Message);
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SecureFlow.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
