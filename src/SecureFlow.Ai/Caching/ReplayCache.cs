using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureFlow.Ai.Caching;

/// <summary>
/// Content-addressed cache of provider responses. Every call is recorded; identical inputs replay from disk.
/// This is what makes the demo survive a flaky network or an API outage: warm the cache during rehearsal,
/// then set SECUREFLOW_AI_OFFLINE=1 if you want to prove it.
/// </summary>
public sealed class ReplayCache
{
    private readonly string _dir;
    private readonly bool _enabled;
    private readonly bool _offlineOnly;

    public ReplayCache(string dir, bool enabled, bool offlineOnly)
    {
        _dir = dir;
        _enabled = enabled;
        _offlineOnly = offlineOnly;
        if (_enabled) Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    public async Task<T> GetOrAddAsync<T>(string operation, string providerName, object keyMaterial, Func<Task<T>> factory, IProgress<string>? progress, CancellationToken ct) where T : class
    {
        if (!_enabled) return await factory();

        var key = Key(operation, providerName, keyMaterial);
        var path = Path.Combine(_dir, $"{operation}-{key}.json");

        if (File.Exists(path))
        {
            var env = JsonSerializer.Deserialize<Envelope<T>>(await File.ReadAllTextAsync(path, ct), AiJson.Options);
            if (env?.Response is not null)
            {
                progress?.Report($"Replayed {operation} from cache ({key[..8]}), originally produced by {env.Provider} at {env.CreatedAt:u}.");
                return env.Response;
            }
        }

        if (_offlineOnly)
            throw new AiUnavailableException($"Offline mode: no cached response for {operation} ({key[..8]}). Run once online to record it.");

        var result = await factory();
        var envelope = new Envelope<T> { Operation = operation, Provider = providerName, CreatedAt = DateTimeOffset.UtcNow, Response = result };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, new JsonSerializerOptions(AiJson.Options) { WriteIndented = true }), ct);
        return result;
    }

    private static string Key(string operation, string providerName, object keyMaterial)
    {
        var material = keyMaterial is string s ? s : AiJson.Serialize(keyMaterial);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}|{providerName}|{material}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class Envelope<T> where T : class
    {
        public string Operation { get; set; } = "";
        public string Provider { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public T? Response { get; set; }
    }
}
