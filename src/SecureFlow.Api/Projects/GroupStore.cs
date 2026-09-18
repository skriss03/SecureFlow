using System.Collections.Concurrent;
using System.Text.Json;
using SecureFlow.Ai;

namespace SecureFlow.Api.Projects;

/// <summary>In-memory store with JSON persistence to data/groups.json, mirroring ProjectStore.</summary>
public sealed class GroupStore
{
    private readonly ConcurrentDictionary<string, Group> _groups = new();
    private readonly string _file;
    private readonly ILogger<GroupStore> _log;
    private readonly object _saveLock = new();
    private static readonly JsonSerializerOptions Json = new(AiJson.Options) { WriteIndented = true };

    public GroupStore(IConfiguration config, IHostEnvironment env, ILogger<GroupStore> log)
    {
        _log = log;
        var dataDir = config["Storage:DataDir"] ?? "data";
        var dir = Path.IsPathRooted(dataDir) ? dataDir : Path.Combine(env.ContentRootPath, dataDir);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "groups.json");
        Load();
        if (_groups.IsEmpty) Seed();
    }

    public IEnumerable<Group> All => _groups.Values.OrderBy(g => g.Name);

    public Group? Get(string id) => _groups.TryGetValue(id, out var g) ? g : null;

    public Group Add(string name)
    {
        var g = new Group { Name = name };
        _groups[g.Id] = g;
        Save();
        return g;
    }

    public bool Delete(string id)
    {
        if (!_groups.TryRemove(id, out _)) return false;
        Save();
        return true;
    }

    private void Seed()
    {
        foreach (var name in new[] { "Platform Team", "Payments Team", "Growth Team" })
        {
            var g = new Group { Name = name };
            _groups[g.Id] = g;
        }
        Save();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_file, JsonSerializer.Serialize(_groups.Values.ToList(), Json)); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not persist groups"); }
        }
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            foreach (var g in JsonSerializer.Deserialize<List<Group>>(File.ReadAllText(_file), Json) ?? new())
                _groups[g.Id] = g;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Skipping unreadable groups file {File}", _file);
        }
    }
}
