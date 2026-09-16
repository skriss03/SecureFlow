using System.Collections.Concurrent;
using System.Text.Json;
using SecureFlow.Ai;
using SecureFlow.Core.Model;

namespace SecureFlow.Api.Projects;

/// <summary>In-memory store with JSON persistence under data/projects so a restart does not lose the demo.</summary>
public sealed class ProjectStore
{
    private readonly ConcurrentDictionary<string, Project> _projects = new();
    private readonly string _dir;
    private readonly ILogger<ProjectStore> _log;
    private static readonly JsonSerializerOptions Json = new(AiJson.Options) { WriteIndented = true };

    public ProjectStore(IConfiguration config, IHostEnvironment env, ILogger<ProjectStore> log)
    {
        _log = log;
        var dataDir = config["Storage:DataDir"] ?? "data";
        _dir = Path.IsPathRooted(dataDir) ? Path.Combine(dataDir, "projects") : Path.Combine(env.ContentRootPath, dataDir, "projects");
        Directory.CreateDirectory(_dir);
        Load();
    }

    public IEnumerable<Project> All => _projects.Values.OrderByDescending(p => p.CreatedAt);

    public Project? Get(string id) => _projects.TryGetValue(id, out var p) ? p : null;

    public Project Add(Project p)
    {
        _projects[p.Id] = p;
        Save(p);
        return p;
    }

    public void Save(Project p)
    {
        p.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            File.WriteAllText(Path.Combine(_dir, p.Id + ".json"), JsonSerializer.Serialize(p, Json));
            if (p.Digest is not null)
                File.WriteAllText(Path.Combine(_dir, p.Id + ".digest.json"), JsonSerializer.Serialize(p.Digest, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist project {Id}", p.Id);
        }
    }

    public bool Delete(string id)
    {
        if (!_projects.TryRemove(id, out _)) return false;
        foreach (var f in new[] { id + ".json", id + ".digest.json" })
        {
            var path = Path.Combine(_dir, f);
            if (File.Exists(path)) File.Delete(path);
        }
        return true;
    }

    private void Load()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json").Where(f => !f.EndsWith(".digest.json")))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(file), Json);
                if (p is null) continue;
                // Anything that was mid-flight when the process died is now stale.
                if (p.Status is not (ProjectStatus.Ready or ProjectStatus.Error))
                {
                    p.Status = ProjectStatus.Error;
                    p.Error = "Interrupted by a server restart.";
                }
                var digestPath = Path.Combine(_dir, p.Id + ".digest.json");
                if (File.Exists(digestPath))
                    p.Digest = JsonSerializer.Deserialize<RepoDigest>(File.ReadAllText(digestPath), Json);
                _projects[p.Id] = p;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping unreadable project file {File}", file);
            }
        }
        _log.LogInformation("Loaded {Count} project(s) from {Dir}", _projects.Count, _dir);
    }
}
