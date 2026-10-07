using System.Text;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Scoring;
using SecureFlow.Ingest;
using SecureFlow.Ingest.Diagrams;
using Xunit;

namespace SecureFlow.Core.Tests;

/// <summary>
/// Tool-agnostic diagram import: format detection, each parser, and the headline guarantee that the same
/// architecture drawn in different tools produces the same model and the same findings.
/// </summary>
public class DiagramImportTests
{
    private static string Sample(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SecureFlow.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? ".", "samples", "diagrams", file);
    }

    private static readonly DiagramImporter Importer = new();

    private static DiagramImportResult Import(string file) => Importer.Import(file, File.ReadAllBytes(Sample(file)));

    private static DiagramImportResult ImportText(string fileName, string text) => Importer.Import(fileName, Encoding.UTF8.GetBytes(text));

    // ---------- detection ----------

    [Theory]
    [InlineData("northbank.drawio", "drawio", DiagramRoute.Parser)]
    [InlineData("northbank.drawio.png", "drawio", DiagramRoute.Parser)]
    [InlineData("northbank.mmd", "mermaid", DiagramRoute.Parser)]
    [InlineData("northbank-c4.puml", "plantuml", DiagramRoute.Parser)]
    [InlineData("northbank.dsl", "structurizr", DiagramRoute.Parser)]
    [InlineData("northbank.vsdx", "visio", DiagramRoute.Parser)]
    [InlineData("northbank.excalidraw", "excalidraw", DiagramRoute.Parser)]
    [InlineData("northbank.gliffy", "gliffy", DiagramRoute.Parser)]
    [InlineData("northbank.png", "image", DiagramRoute.AiImage)]
    [InlineData("northbank.pdf", "pdf", DiagramRoute.AiPdf)]
    [InlineData("northbank.svg", "svg", DiagramRoute.AiText)]
    public void Every_sample_is_detected_by_content_and_extension(string file, string format, DiagramRoute route)
    {
        var d = Importer.Detect(file, File.ReadAllBytes(Sample(file)));
        Assert.Equal(format, d.Format);
        Assert.Equal(route, d.Route);
    }

    [Fact]
    public void Content_wins_over_a_misleading_extension()
    {
        // A Mermaid diagram saved as .txt, and a draw.io file saved as .xml, are still parsed exactly.
        Assert.Equal("mermaid", ImportText("diagram.txt", "flowchart LR\n  a[Web] --> b[(Orders DB)]\n").Detection.Format);
        Assert.Equal("drawio", Importer.Detect("export.xml", File.ReadAllBytes(Sample("northbank.drawio"))).Format);
    }

    [Theory]
    [InlineData("legacy.vsd", "Save As")]
    [InlineData("board.fig", "Export")]
    public void Closed_formats_are_rejected_with_a_way_forward(string file, string hint)
    {
        var d = Importer.Detect(file, [1, 2, 3, 4]);
        Assert.Equal(DiagramRoute.Rejected, d.Route);
        Assert.Contains(hint, d.Message);
    }

    [Fact]
    public void Binary_files_of_no_known_format_are_rejected()
    {
        var d = Importer.Detect("blob.bin", [0, 1, 2, 3, 0, 5]);
        Assert.Equal(DiagramRoute.Rejected, d.Route);
    }

    [Fact]
    public void Unknown_diagram_languages_go_to_the_ai_but_other_text_does_not()
    {
        // D2 and Graphviz have no parser here, but they are diagrams: the AI reads them.
        Assert.Equal(DiagramRoute.AiText, Importer.Detect("system.d2", Encoding.UTF8.GetBytes("web -> api: HTTPS\napi -> db\n")).Route);
        Assert.Equal(DiagramRoute.AiText, Importer.Detect("system.dot", Encoding.UTF8.GetBytes("digraph { web -> api; api -> db }")).Route);
        // A text file with no edges is not a diagram; refusing it saves a pointless AI call.
        var notADiagram = Importer.Detect("SecureFlow.slnx", Encoding.UTF8.GetBytes("<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n"));
        Assert.Equal(DiagramRoute.Rejected, notADiagram.Route);
    }

    [Fact]
    public void Formats_can_be_disabled_by_configuration()
    {
        var importer = new DiagramImporter(new DiagramOptions { DisabledFormats = ["mermaid", "pdf"] });
        Assert.NotEqual("mermaid", importer.Detect("x.mmd", File.ReadAllBytes(Sample("northbank.mmd"))).Format);
        Assert.Equal(DiagramRoute.Rejected, importer.Detect("x.pdf", File.ReadAllBytes(Sample("northbank.pdf"))).Route);
        Assert.DoesNotContain(importer.SupportedFormats(), f => f.Format is "mermaid" or "pdf");
    }

    // ---------- the headline guarantee ----------

    public static TheoryData<string> StructuredSamples => new()
    {
        "northbank.drawio", "northbank.drawio.png", "northbank.mmd", "northbank-c4.puml", "northbank.dsl",
        "northbank.vsdx", "northbank.excalidraw", "northbank.gliffy",
    };

    [Theory]
    [MemberData(nameof(StructuredSamples))]
    public void Same_architecture_in_any_tool_gives_the_same_model_and_findings(string file)
    {
        var reference = Import("northbank.mmd").Model!;
        var model = Import(file).Model;
        Assert.NotNull(model);

        Assert.Equal(Components(reference), Components(model!));
        Assert.Equal(Flows(reference), Flows(model!));
        // Every format places the services in the same zones (Structurizr also adds the software-system boundary).
        foreach (var b in reference.TrustBoundaries)
            Assert.Contains(model!.TrustBoundaries, mb => mb.Name == b.Name && mb.ComponentIds.Order().SequenceEqual(b.ComponentIds.Order()));

        var expected = RuleCatalog.Evaluate(reference);
        var actual = RuleCatalog.Evaluate(model!);
        Assert.Equal(expected.Select(f => f.RuleId).Order(), actual.Select(f => f.RuleId).Order());
        Assert.Equal(Scorer.Score(expected).Resilience, Scorer.Score(actual).Resilience);
        Assert.Equal(Scorer.Score(expected).Security, Scorer.Score(actual).Security);
    }

    private static List<string> Components(ArchitectureModel m) =>
        m.Components.Select(c => $"{c.Id}:{c.Type}:{c.Props.Replicas}").Order().ToList();

    private static List<string> Flows(ArchitectureModel m) =>
        m.Flows.Select(f => $"{f.From}->{f.To}:{f.Protocol}:{f.Auth}:{f.IsSync}").Order().ToList();

    [Fact]
    public void Northbank_findings_are_the_ones_the_design_should_show()
    {
        var model = Import("northbank.vsdx").Model!;
        var findings = RuleCatalog.Evaluate(model);
        Assert.Contains(findings, f => f.RuleId == "R01" && f.ComponentIds.Contains("payments-db"));   // single database
        Assert.Contains(findings, f => f.RuleId == "R01" && f.ComponentIds.Contains("fraud-service")); // single fraud service
        Assert.Contains(findings, f => f.RuleId == "R04");                                            // sync calls without timeouts
        Assert.Equal("none", model.FindFlow(model.Flows.Single(f => f.To == "fraud-service").Id)!.Auth); // unauthenticated hop
    }

    // ---------- parser details ----------

    [Fact]
    public void Text_formats_cite_file_and_line_as_evidence()
    {
        var model = Import("northbank.mmd").Model!;
        var db = model.FindComponent("payments-db")!;
        var ev = Assert.Single(db.Evidence);
        Assert.Equal("mermaid", ev.Source);
        Assert.Equal("northbank.mmd", ev.Path);
        var lines = File.ReadAllLines(Sample("northbank.mmd"));
        Assert.Contains("Payments DB", lines[ev.Line!.Value - 1]);
    }

    [Fact]
    public void Mermaid_reads_inline_labels_chains_fan_out_and_new_shape_syntax()
    {
        var model = ImportText("x.mmd", """
            graph TD
              web[Storefront] -- HTTPS --> api[Orders API] --> cache[(Redis cache)]
              api & web -.->|publish| q[/Events queue/]
              db@{ shape: cyl, label: "Orders DB" }
              api --> db
            """).Model!;
        Assert.Equal(ComponentType.WebApp, model.FindComponent("storefront")!.Type);
        Assert.Equal(ComponentType.Database, model.FindComponent("orders-db")!.Type);
        Assert.Contains(model.Flows, f => f.From == "storefront" && f.To == "orders-api" && f.Protocol == "https");
        Assert.Contains(model.Flows, f => f.From == "orders-api" && f.To == "redis-cache");
        Assert.Equal(2, model.Flows.Count(f => f.To == "events-queue" && f.IsSync == false));
    }

    [Fact]
    public void Mermaid_blocks_inside_markdown_are_read()
    {
        var md = "# Architecture\n\nSome text.\n\n```mermaid\nflowchart LR\n  a[Web App] --> b[(Main DB)]\n```\n";
        var result = ImportText("README.md", md);
        Assert.Equal("mermaid", result.Detection.Format);
        Assert.Equal(7, result.Model!.FindComponent("web-app")!.Evidence[0].Line);
    }

    [Fact]
    public void Plain_PlantUML_component_diagrams_are_read()
    {
        var model = ImportText("x.puml", """
            @startuml
            actor User
            node "Kubernetes" {
              [Orders API] as api
              database "Orders DB" as db
            }
            queue Events
            User --> api : HTTPS
            api ..> Events : publish
            api --> db
            @enduml
            """).Model!;
        Assert.Equal(ComponentType.User, model.FindComponent("user")!.Type);
        Assert.Equal(ComponentType.Database, model.FindComponent("orders-db")!.Type);
        Assert.Contains(model.TrustBoundaries, b => b.Name == "Kubernetes" && b.ComponentIds.Contains("orders-api"));
        Assert.Contains(model.Flows, f => f.To == "events" && f.IsSync == false);
    }

    [Fact]
    public void Extra_type_keywords_come_from_configuration()
    {
        var text = "flowchart LR\n  a[Web] --> b[HANA store]\n";
        Assert.NotEqual(ComponentType.Database, ImportText("x.mmd", text).Model!.FindComponent("hana-store")!.Type);
        var configured = new DiagramImporter(new DiagramOptions { ExtraTypeKeywords = new() { ["Database"] = ["hana"] } });
        Assert.Equal(ComponentType.Database, configured.Import("x.mmd", Encoding.UTF8.GetBytes(text)).Model!.FindComponent("hana-store")!.Type);
    }

    // ---------- AI routes and fallback ----------

    [Fact]
    public void A_structured_file_with_nothing_usable_falls_back_to_the_ai()
    {
        var result = ImportText("x.mmd", "flowchart LR\n%% nothing here yet\n");
        Assert.Null(result.Model);
        Assert.True(result.NeedsAi);
        Assert.Equal(DiagramRoute.AiText, result.Detection.Route);
    }

    [Fact]
    public void Fallback_can_be_turned_off()
    {
        var importer = new DiagramImporter(new DiagramOptions { AiFallback = false });
        var result = importer.Import("x.mmd", Encoding.UTF8.GetBytes("flowchart LR\n"));
        Assert.Equal(DiagramRoute.Rejected, result.Detection.Route);
    }

    [Fact]
    public void Svg_sent_to_the_ai_has_embedded_images_stripped_and_is_capped()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"data:image/png;base64," + new string('A', 5000) + "\"/><text>Orders API</text></svg>";
        var importer = new DiagramImporter(new DiagramOptions { MaxAiTextChars = 1000 });
        var result = importer.Import("figma-export.svg", Encoding.UTF8.GetBytes(svg));
        var sent = Encoding.UTF8.GetString(result.AiContent!);
        Assert.DoesNotContain("AAAAAAAAAA", sent);
        Assert.Contains("Orders API", sent);
        Assert.True(sent.Length <= 1000);
    }

    [Fact]
    public void Oversized_files_are_refused_before_parsing()
    {
        var importer = new DiagramImporter(new DiagramOptions { MaxUploadBytes = 10 });
        Assert.Equal(DiagramRoute.Rejected, importer.Import("x.mmd", Encoding.UTF8.GetBytes("flowchart LR\n a-->b\n")).Detection.Route);
    }

    [Fact]
    public void The_old_drawio_entry_point_still_works()
    {
        var model = DrawioIngest.Parse(File.ReadAllText(Sample("northbank.drawio")), "northbank.drawio");
        Assert.Equal(10, model.Components.Count);
    }
}
