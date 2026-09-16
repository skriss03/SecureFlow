using SecureFlow.Core.Model;

namespace SecureFlow.Core.Scoring;

public sealed class ScoreCard
{
    public int Resilience { get; set; }
    public int Security { get; set; }
    public int Overall => (Resilience + Security) / 2;
    public string ResilienceGrade => Grade(Resilience);
    public string SecurityGrade => Grade(Security);
    public Dictionary<Severity, int> OpenBySeverity { get; set; } = new();
    /// <summary>0..1 per component: how much of the total risk weight it carries.</summary>
    public Dictionary<string, double> ComponentHeat { get; set; } = new();

    public static string Grade(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };
}

public static class Scorer
{
    public static double Weight(Severity s) => s switch
    {
        Severity.Critical => 15,
        Severity.High => 8,
        Severity.Medium => 4,
        Severity.Low => 1,
        _ => 0,
    };

    public static double ConfidenceFactor(Confidence c) => c switch
    {
        Confidence.High => 1.0,
        Confidence.Medium => 0.7,
        _ => 0.4,
    };

    public static double Weight(Finding f) => Weight(f.Severity) * ConfidenceFactor(f.Confidence);

    public static ScoreCard Score(IEnumerable<Finding> findings)
    {
        var open = findings.Where(f => f.Status == FindingStatus.Open).ToList();
        var card = new ScoreCard
        {
            Resilience = Curve(open.Where(f => f.Category == FindingCategory.Resilience)),
            Security = Curve(open.Where(f => f.Category == FindingCategory.Security)),
        };

        foreach (Severity s in Enum.GetValues<Severity>())
            card.OpenBySeverity[s] = open.Count(f => f.Severity == s);

        var heat = new Dictionary<string, double>();
        foreach (var f in open)
            foreach (var id in f.ComponentIds.Distinct())
                heat[id] = heat.GetValueOrDefault(id) + Weight(f);
        var max = heat.Count == 0 ? 1 : heat.Values.Max();
        card.ComponentHeat = heat.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / max, 3));
        return card;
    }

    /// <summary>Decay constant: total weight at which the score falls to ~37. Tuned so a badly flawed model lands in the 30s.</summary>
    private const double K = 140;

    /// <summary>
    /// Diminishing curve so the 40th finding still moves the needle, plus hard caps:
    /// an open Critical finding cannot score above 49 (D), an open High cannot score above 74 (C).
    /// </summary>
    private static int Curve(IEnumerable<Finding> open)
    {
        var list = open.ToList();
        var score = 100 * Math.Exp(-list.Sum(Weight) / K);
        if (list.Any(f => f.Severity == Severity.Critical)) score = Math.Min(score, 49);
        else if (list.Any(f => f.Severity == Severity.High)) score = Math.Min(score, 74);
        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }
}
