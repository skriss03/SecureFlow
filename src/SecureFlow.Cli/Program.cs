using System.Text.Json;
using SecureFlow.Core.Model;
using SecureFlow.Core.Rules;
using SecureFlow.Core.Samples;
using SecureFlow.Core.Scoring;
using SecureFlow.Core.Simulation;

// secureflow analyze --sample shopfast|healthy
// secureflow analyze --model path/to/model.json [--json] [--fail-on high]
// secureflow simulate --sample shopfast --kill orders-db
// secureflow rules
//
// Exit code 0 = ok, 2 = findings at or above --fail-on (for CI gates).

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var args_ = args.ToList();
if (args_.Count == 0 || args_[0] is "-h" or "--help") { Usage(); return 0; }

string Opt(string name, string? def = null)
{
    var i = args_.IndexOf(name);
    return i >= 0 && i + 1 < args_.Count ? args_[i + 1] : def ?? "";
}
bool Flag(string name) => args_.Contains(name);

ArchitectureModel LoadModel()
{
    var path = Opt("--model");
    if (!string.IsNullOrEmpty(path))
        return JsonSerializer.Deserialize<ArchitectureModel>(File.ReadAllText(path), json) ?? throw new InvalidOperationException("Empty model");
    return Opt("--sample", "shopfast").ToLowerInvariant() switch
    {
        "healthy" => SampleModels.Healthy(),
        _ => SampleModels.ShopFast(),
    };
}

switch (args_[0])
{
    case "rules":
        foreach (var r in RuleCatalog.Describe())
            Console.WriteLine($"{r.Id}  {r.Category,-10} {r.DefaultSeverity,-8} {r.Tag,-24} {r.Title}");
        return 0;

    case "analyze":
    {
        var model = LoadModel();
        var findings = RuleCatalog.Evaluate(model);
        var score = Scorer.Score(findings);

        if (Flag("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { model.Name, score, findings }, json));
        }
        else
        {
            Console.WriteLine($"{model.Name}: Resilience {score.Resilience} ({score.ResilienceGrade})  Security {score.Security} ({score.SecurityGrade})");
            Console.WriteLine($"Open: {string.Join("  ", score.OpenBySeverity.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
            Console.WriteLine();
            foreach (var f in findings)
                Console.WriteLine($"{f.Severity,-8} {f.Confidence,-6} {f.RuleId} {f.Title}: {f.Description}");
            if (Flag("--weights"))
            {
                Console.WriteLine();
                foreach (var cat in new[] { FindingCategory.Resilience, FindingCategory.Security })
                    Console.WriteLine($"{cat} total weight = {findings.Where(f => f.Category == cat).Sum(Scorer.Weight):F1}");
            }
        }

        var failOn = Opt("--fail-on");
        if (Enum.TryParse<Severity>(failOn, true, out var threshold) && findings.Any(f => f.Severity >= threshold))
            return 2;
        return 0;
    }

    case "simulate":
    {
        var model = LoadModel();
        var kill = Opt("--kill");
        if (string.IsNullOrEmpty(kill)) { Console.Error.WriteLine("--kill <componentId> required"); return 1; }
        var r = BlastRadius.Simulate(model, kill);
        Console.WriteLine(r.Summary);
        foreach (var i in r.Impacts)
            Console.WriteLine($"{i.Level,-9} {i.Name,-28} {i.Reason}");
        return 0;
    }

    default:
        Usage();
        return 1;
}

static void Usage()
{
    Console.WriteLine("""
        secureflow analyze  [--sample shopfast|healthy | --model model.json] [--json] [--weights] [--fail-on critical|high|medium]
        secureflow simulate [--sample ... | --model ...] --kill <componentId>
        secureflow rules
        """);
}
