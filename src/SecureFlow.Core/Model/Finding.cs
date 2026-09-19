using System.Text.Json.Serialization;

namespace SecureFlow.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<FindingCategory>))]
public enum FindingCategory { Resilience, Security, Vulnerability }

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Info = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence { Low, Medium, High }

[JsonConverter(typeof(JsonStringEnumConverter<FindingSource>))]
public enum FindingSource { Rule, Ai }

[JsonConverter(typeof(JsonStringEnumConverter<FindingStatus>))]
public enum FindingStatus { Open, Fixed, Accepted }

public sealed class Finding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string RuleId { get; set; } = "";
    public FindingCategory Category { get; set; }
    /// <summary>Resilience class (SPOF, Timeout, Cascade...) or STRIDE name (Spoofing, Tampering...).</summary>
    public string Tag { get; set; } = "";
    public Severity Severity { get; set; }
    public Confidence Confidence { get; set; } = Confidence.High;
    public FindingSource Source { get; set; } = FindingSource.Rule;
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public string Title { get; set; } = "";
    /// <summary>What was observed in this model, naming the components.</summary>
    public string Description { get; set; } = "";
    /// <summary>Why it matters.</summary>
    public string Rationale { get; set; } = "";
    public string Mitigation { get; set; } = "";
    public List<string> ComponentIds { get; set; } = new();
    public List<string> FlowIds { get; set; } = new();
    public List<Evidence> Evidence { get; set; } = new();
    /// <summary>Hint for the fixer, e.g. "set flow.timeoutMs".</summary>
    public string? FixHint { get; set; }
}
