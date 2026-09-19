using System.Net.Http.Headers;
using System.Text.Json;

namespace SecureFlow.Ingest;

public sealed record DiscoveredRepo(string Name, string CloneUrl, string? DefaultBranch, bool Fork, bool Archived, DateTimeOffset? PushedAt);

/// <summary>
/// Lists the repositories under a GitHub org or user "root" URL (e.g. https://github.com/acme),
/// so scanning a whole team's portfolio doesn't require pasting in one repo URL at a time.
/// GitHub only for now — the other three catalogue repos in this app are all GitHub too.
/// </summary>
public sealed class GitHostDiscovery
{
    private readonly HttpClient _http;

    /// <summary>Hard cap so pointing this at a huge org can't kick off an unbounded scan.</summary>
    public int MaxRepos { get; init; } = 20;

    public GitHostDiscovery(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SecureFlow", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// A "root" is an org/user URL with no repo segment: https://github.com/&lt;owner&gt; or
    /// https://github.com/&lt;owner&gt;/. A specific repo URL (an extra path segment) is not a root.
    /// </summary>
    public static bool TryGetOwner(string url, out string owner)
    {
        owner = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1) return false;
        owner = segments[0];
        return !string.IsNullOrWhiteSpace(owner);
    }

    /// <summary>Most-recently-pushed, non-fork, non-archived repos first, capped at <see cref="MaxRepos"/>.</summary>
    public async Task<List<DiscoveredRepo>> ListReposAsync(string owner, CancellationToken ct)
    {
        var all = await FetchAsync($"orgs/{owner}/repos?per_page=100&type=public", ct)
                  ?? await FetchAsync($"users/{owner}/repos?per_page=100&type=owner", ct)
                  ?? throw new InvalidOperationException($"No GitHub org or user named '{owner}' (or it has no public repositories).");

        return all
            .Where(r => !r.Fork && !r.Archived)
            .OrderByDescending(r => r.PushedAt)
            .Take(MaxRepos)
            .ToList();
    }

    private async Task<List<DiscoveredRepo>?> FetchAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"GitHub API returned {(int)res.StatusCode} for {path}: {Trunc(body)}");
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var repos = new List<DiscoveredRepo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            repos.Add(new DiscoveredRepo(
                Name: el.GetProperty("name").GetString() ?? "",
                CloneUrl: el.GetProperty("clone_url").GetString() ?? "",
                DefaultBranch: el.TryGetProperty("default_branch", out var b) ? b.GetString() : null,
                Fork: el.TryGetProperty("fork", out var f) && f.GetBoolean(),
                Archived: el.TryGetProperty("archived", out var a) && a.GetBoolean(),
                PushedAt: el.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(p.GetString()!) : null));
        }
        return repos;
    }

    private static string Trunc(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
