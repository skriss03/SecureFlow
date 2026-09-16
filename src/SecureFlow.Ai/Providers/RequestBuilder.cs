using System.Text;
using SecureFlow.Core.Model;

namespace SecureFlow.Ai.Providers;

/// <summary>Builds the provider-neutral parts of each request: system prompt, user text, schema name.</summary>
internal static class RequestBuilder
{
    public sealed record Request(string System, string UserText, string SchemaName, string Operation);

    public static Request ExtractImage(string? hint)
    {
        var sb = new StringBuilder("Extract the architecture model from the attached diagram.");
        if (!string.IsNullOrWhiteSpace(hint)) sb.Append("\n\nContext from the user: ").Append(hint.Trim());
        return new Request(AiJson.Prompt("extract-image"), sb.ToString(), "architecture-model", "extract-image");
    }

    public static Request ExtractRepo(RepoDigest digest)
    {
        var sb = new StringBuilder();
        sb.Append("Repository: ").Append(digest.RepoUrl);
        if (digest.Branch is not null) sb.Append(" (branch ").Append(digest.Branch).Append(')');
        if (digest.Commit is not null) sb.Append(" at ").Append(digest.Commit);
        sb.Append("\nFiles in repository: ").Append(digest.TotalFilesInRepo)
          .Append("; included below: ").Append(digest.Files.Count).Append('\n');
        foreach (var n in digest.SkippedNotes) sb.Append("Note: ").Append(n).Append('\n');
        sb.Append("\n=== DIRECTORY TREE ===\n").Append(digest.Tree).Append('\n');
        foreach (var f in digest.Files)
        {
            sb.Append("\n=== FILE: ").Append(f.Path).Append(f.Truncated ? " (truncated) ===\n" : " ===\n");
            var lines = f.Content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                sb.Append(i + 1).Append(": ").Append(lines[i].TrimEnd('\r')).Append('\n');
        }
        sb.Append("\nExtract the architecture model. Cite path and line numbers from the files above as evidence.");
        return new Request(AiJson.Prompt("extract-repo"), sb.ToString(), "architecture-model", "extract-repo");
    }

    public static Request Analyze(ArchitectureModel model, IReadOnlyList<Finding> ruleFindings)
    {
        var findings = ruleFindings.Select(f => new
        {
            f.Id, f.RuleId, f.Category, f.Tag, f.Severity, f.Confidence, f.Title, f.Description, f.ComponentIds, f.FlowIds,
        });
        var text = $"""
            === ARCHITECTURE MODEL ===
            {AiJson.Serialize(model)}

            === RULE FINDINGS ({ruleFindings.Count}) ===
            {AiJson.Serialize(findings)}

            Review this system and produce the analysis.
            """;
        return new Request(AiJson.Prompt("analyze"), text, "analysis", "analyze");
    }

    public static Request Fix(ArchitectureModel model, Finding target, IReadOnlyList<Finding> allFindings, RepoDigest? digest)
    {
        var others = allFindings.Where(f => f.Id != target.Id && f.Status == FindingStatus.Open)
            .Select(f => new { f.Id, f.RuleId, f.Severity, f.Title, f.Description, f.ComponentIds, f.FlowIds, f.FixHint });
        var sb = new StringBuilder();
        sb.Append("=== ARCHITECTURE MODEL ===\n").Append(AiJson.Serialize(model)).Append('\n');
        sb.Append("\n=== TARGET FINDING ===\n").Append(AiJson.Serialize(new
        {
            target.Id, target.RuleId, target.Category, target.Tag, target.Severity, target.Title, target.Description,
            target.Rationale, target.Mitigation, target.ComponentIds, target.FlowIds, target.Evidence, target.FixHint,
        })).Append('\n');
        sb.Append("\n=== OTHER OPEN FINDINGS ===\n").Append(AiJson.Serialize(others)).Append('\n');

        if (digest is not null)
        {
            // Only the files the finding's evidence points at, to keep the fix grounded and the request small.
            var paths = target.Evidence.Select(e => e.Path).Where(p => p is not null).Distinct().ToHashSet();
            var files = digest.Files.Where(f => paths.Contains(f.Path)).ToList();
            if (files.Count > 0)
            {
                sb.Append("\n=== RELEVANT SOURCE FILES ===\n");
                foreach (var f in files)
                {
                    sb.Append("--- ").Append(f.Path).Append(" ---\n");
                    var lines = f.Content.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                        sb.Append(i + 1).Append(": ").Append(lines[i].TrimEnd('\r')).Append('\n');
                }
            }
        }
        sb.Append("\nPropose the fix for the target finding.");
        return new Request(AiJson.Prompt("fix"), sb.ToString(), "fix", "fix");
    }
}
