using System.Text.Json.Serialization;
using SecureFlow.Ai;
using SecureFlow.Core.Model;
using SecureFlow.Core.Scoring;

namespace SecureFlow.Api.Projects;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
public enum ProjectStatus { Queued, Extracting, Analyzing, Reviewing, Ready, Error }

public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>image | drawio | repo | sample | json</summary>
    public string Source { get; set; } = "";
    public string? SourceRef { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Queued;
    public string? Error { get; set; }
    public string? AiProvider { get; set; }

    public ArchitectureModel Model { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public ScoreCard Score { get; set; } = new();
    public AiAnalysis? Analysis { get; set; }
    public List<HistoryPoint> History { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public Dictionary<string, FixProposal> Proposals { get; set; } = new();

    /// <summary>Kept out of the API response; large. Used to ground fixes in source.</summary>
    [JsonIgnore]
    public RepoDigest? Digest { get; set; }

    private readonly object _mutationLock = new();

    // The pipeline runs on a background thread while request threads serialize this whole object
    // (GET /api/projects/{id} and ProjectStore.Save). Adding to a List<T> that is being enumerated
    // throws InvalidOperationException, so every collection the pipeline appends to is copy-on-write:
    // writers swap in a new instance under the lock and readers only ever see a finished list.
    // Readers take no lock, so they may see a list one write behind; for a progress log and a score
    // history that is fine, and it keeps serialization off the pipeline's critical path.

    public void AddLog(string message, string level = "info")
    {
        var entry = new LogEntry { At = DateTimeOffset.UtcNow, Level = level, Message = message };
        lock (_mutationLock)
        {
            Log = new List<LogEntry>(Log) { entry };
        }
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Entries from index <paramref name="from"/> onward, safe to call while the pipeline appends.</summary>
    public List<LogEntry> LogSince(int from)
    {
        var log = Log;
        return from >= log.Count ? new List<LogEntry>() : log.Skip(from).ToList();
    }

    public void Snapshot(string action)
    {
        var point = new HistoryPoint
        {
            At = DateTimeOffset.UtcNow,
            Action = action,
            Resilience = Score.Resilience,
            Security = Score.Security,
            OpenFindings = Findings.Count(f => f.Status == FindingStatus.Open),
        };
        lock (_mutationLock)
        {
            History = new List<HistoryPoint>(History) { point };
        }
    }

    public void SetProposal(string findingId, FixProposal proposal)
    {
        lock (_mutationLock)
        {
            Proposals = new Dictionary<string, FixProposal>(Proposals) { [findingId] = proposal };
        }
    }
}

public sealed class LogEntry
{
    public DateTimeOffset At { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class HistoryPoint
{
    public DateTimeOffset At { get; set; }
    public string Action { get; set; } = "";
    public int Resilience { get; set; }
    public int Security { get; set; }
    public int OpenFindings { get; set; }
}

/// <summary>Lightweight row for the project list.</summary>
public sealed record ProjectSummary(string Id, string Name, string Source, ProjectStatus Status, DateTimeOffset CreatedAt, int Resilience, int Security, int OpenFindings, int Components);
