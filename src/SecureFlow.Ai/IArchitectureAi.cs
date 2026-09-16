using System.Text.Json;
using System.Text.Json.Serialization;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai;

/// <summary>
/// The only contract the rest of the system depends on. Three extraction/analysis operations plus a fixer.
/// Implemented by <see cref="Providers.AnthropicArchitectureAi"/> (primary) and <see cref="Providers.OpenAiArchitectureAi"/>;
/// wrapped by <see cref="Caching.CachingArchitectureAi"/> so demos never depend on a live network call.
/// </summary>
public interface IArchitectureAi
{
    /// <summary>Provider and model, for the UI badge: "claude-opus-5 (Anthropic)".</summary>
    string Name { get; }

    Task<ArchitectureModel> ExtractFromImageAsync(byte[] image, string mediaType, string? hint, IProgress<string>? progress, CancellationToken ct);
    Task<ArchitectureModel> ExtractFromRepoDigestAsync(RepoDigest digest, IProgress<string>? progress, CancellationToken ct);
    Task<AiAnalysis> AnalyzeAsync(ArchitectureModel model, IReadOnlyList<Finding> ruleFindings, IProgress<string>? progress, CancellationToken ct);
    Task<FixProposal> ProposeFixAsync(ArchitectureModel model, Finding target, IReadOnlyList<Finding> allFindings, RepoDigest? digest, IProgress<string>? progress, CancellationToken ct);
}

// ---------- Inputs ----------

public sealed class RepoDigest
{
    public string RepoUrl { get; set; } = "";
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    /// <summary>Indented directory listing, so the model sees structure even for files we did not include.</summary>
    public string Tree { get; set; } = "";
    public List<RepoFile> Files { get; set; } = new();
    public int TotalFilesInRepo { get; set; }
    public long IncludedBytes { get; set; }
    public List<string> SkippedNotes { get; set; } = new();
}

public sealed class RepoFile
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Truncated { get; set; }
}

// ---------- Outputs ----------

public sealed class AiAnalysis
{
    public string ExecutiveSummary { get; set; } = "";
    public string BusinessImpact { get; set; } = "";
    public List<AiRisk> TopRisks { get; set; } = new();
    public List<AiFinding> AdditionalFindings { get; set; } = new();
    public List<string> QuickWins { get; set; } = new();
}

public sealed class AiRisk
{
    public string Title { get; set; } = "";
    public string Why { get; set; } = "";
    public List<string> ComponentIds { get; set; } = new();
    public List<string> RelatedFindingIds { get; set; } = new();
}

public sealed class AiFinding
{
    public string Title { get; set; } = "";
    public FindingCategory Category { get; set; }
    public string Tag { get; set; } = "";
    public Severity Severity { get; set; }
    public string Description { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Mitigation { get; set; } = "";
    public List<string> ComponentIds { get; set; } = new();
    public List<string> FlowIds { get; set; } = new();

    public Finding ToFinding()
    {
        var id = Core.Rules.Rule.StableId("AI:" + Title, ComponentIds, FlowIds);
        return new Finding
        {
            Id = id,
            RuleId = "AI",
            Category = Category,
            Tag = Tag,
            Severity = Severity,
            Confidence = Confidence.Medium,
            Source = FindingSource.Ai,
            Title = Title,
            Description = Description,
            Rationale = Rationale,
            Mitigation = Mitigation,
            ComponentIds = ComponentIds,
            FlowIds = FlowIds,
            Evidence = new List<Evidence> { new() { Source = "ai", Snippet = "Contextual finding produced by the AI reviewer from the architecture model." } },
        };
    }
}

public sealed class FixProposal
{
    public string FindingId { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Explanation { get; set; } = "";
    public ModelPatch Patch { get; set; } = new();
    public List<CodeChange> CodeChanges { get; set; } = new();
    public List<string> ResolvesFindingIds { get; set; } = new();
    public string ResidualRisk { get; set; } = "";
}

public sealed class CodeChange
{
    public string? Path { get; set; }
    public string Language { get; set; } = "";
    public string Description { get; set; } = "";
    public string Snippet { get; set; } = "";
}

// ---------- Wire shape for the fix patch (schema-friendly: no open objects) ----------

public sealed class AiFixResponse
{
    public string Summary { get; set; } = "";
    public string Explanation { get; set; } = "";
    public AiPatch Patch { get; set; } = new();
    public List<CodeChange> CodeChanges { get; set; } = new();
    public List<string> ResolvesFindingIds { get; set; } = new();
    public string ResidualRisk { get; set; } = "";

    public FixProposal ToProposal(string findingId) => new()
    {
        FindingId = findingId,
        Summary = Summary,
        Explanation = Explanation,
        Patch = Patch.ToModelPatch(),
        CodeChanges = CodeChanges,
        ResolvesFindingIds = ResolvesFindingIds,
        ResidualRisk = ResidualRisk,
    };
}

public sealed class AiPatch
{
    public List<AiPatchOp> Ops { get; set; } = new();

    public ModelPatch ToModelPatch() => new() { Ops = Ops.Select(o => o.ToPatchOp()).ToList() };
}

public sealed class AiPatchOp
{
    public string Kind { get; set; } = "";
    public string? Id { get; set; }
    public string? Property { get; set; }
    public JsonElement? Value { get; set; }
    /// <summary>Full component or flow as a JSON string, for add-* ops.</summary>
    public string? ObjectJson { get; set; }

    private static readonly HashSet<string> BoolProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "hasBackup", "hasHealthCheck", "autoscale", "encryptionAtRest", "publiclyExposed", "managed",
        "rateLimited", "auditLogging", "inputValidation", "hardcodedSecrets", "isSync", "circuitBreaker", "encrypted",
    };
    private static readonly HashSet<string> IntProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "replicas", "zones", "regions", "timeoutMs", "retries",
    };

    public PatchOp ToPatchOp()
    {
        JsonElement? value = Value;
        // Models sometimes send "true" or "3000" as strings; coerce to the property's real type.
        if (value is { ValueKind: JsonValueKind.String } v && Property is not null)
        {
            var s = v.GetString() ?? "";
            if (BoolProps.Contains(Property) && bool.TryParse(s, out var b)) value = JsonSerializer.SerializeToElement(b);
            else if (IntProps.Contains(Property) && int.TryParse(s, out var i)) value = JsonSerializer.SerializeToElement(i);
        }
        JsonElement? obj = null;
        if (!string.IsNullOrWhiteSpace(ObjectJson))
            obj = JsonSerializer.Deserialize<JsonElement>(ObjectJson);
        return new PatchOp { Kind = Kind, Id = Id, Property = Property, Value = value, Object = obj };
    }
}
