using SecureFlow.Core.Model;

namespace SecureFlow.Core.Rules;

/// <summary>
/// Read-only graph view over an <see cref="ArchitectureModel"/> with the queries rules need.
/// Built once per evaluation; cheap for POC-sized models.
/// </summary>
public sealed class ModelGraph
{
    public ArchitectureModel Model { get; }
    public IReadOnlyDictionary<string, Component> ById { get; }

    private readonly Dictionary<string, List<Flow>> _inbound = new();
    private readonly Dictionary<string, List<Flow>> _outbound = new();
    private readonly Dictionary<string, string> _boundaryOf = new();

    public ModelGraph(ArchitectureModel model)
    {
        Model = model;
        ById = model.Components
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var c in model.Components)
        {
            _inbound[c.Id] = new List<Flow>();
            _outbound[c.Id] = new List<Flow>();
        }

        foreach (var f in model.Flows)
        {
            if (!ById.ContainsKey(f.From) || !ById.ContainsKey(f.To)) continue;
            _outbound[f.From].Add(f);
            _inbound[f.To].Add(f);
        }

        foreach (var b in model.TrustBoundaries)
            foreach (var id in b.ComponentIds)
                _boundaryOf[id] = b.Id;
    }

    public IReadOnlyList<Flow> Inbound(string id) => _inbound.TryGetValue(id, out var l) ? l : Array.Empty<Flow>();
    public IReadOnlyList<Flow> Outbound(string id) => _outbound.TryGetValue(id, out var l) ? l : Array.Empty<Flow>();

    public Component From(Flow f) => ById[f.From];
    public Component To(Flow f) => ById[f.To];

    public string? BoundaryOf(string id) => _boundaryOf.TryGetValue(id, out var b) ? b : null;

    /// <summary>
    /// True when a flow crosses a trust boundary. When the model declares no boundaries,
    /// we fall back to a sensible default: users and external systems are outside, everything else inside.
    /// </summary>
    public bool CrossesBoundary(Flow f)
    {
        if (Model.TrustBoundaries.Count == 0)
            return IsActor(From(f)) != IsActor(To(f));
        return BoundaryOf(f.From) != BoundaryOf(f.To);
    }

    public static bool IsActor(Component c) => c.Type is ComponentType.User or ComponentType.External;
    public static bool IsService(Component c) => c.Type is ComponentType.WebApp or ComponentType.Api or ComponentType.Worker or ComponentType.Gateway;
    public static bool IsDataStore(Component c) => c.Type is ComponentType.Database or ComponentType.Cache or ComponentType.Queue or ComponentType.ObjectStorage;
    public static bool IsEdge(Component c) => c.Type is ComponentType.Gateway or ComponentType.LoadBalancer or ComponentType.Cdn;
    public static bool IsInfrastructure(Component c) => IsService(c) || IsDataStore(c) || c.Type is ComponentType.Identity or ComponentType.Secrets or ComponentType.LoadBalancer;

    /// <summary>Unknown is treated as synchronous: request/response is the common case and the riskier one.</summary>
    public static bool IsSync(Flow f) => f.IsSync != false;

    public bool IsPublic(Component c) =>
        c.Props.PubliclyExposed == true || Inbound(c.Id).Any(f => From(f).Type == ComponentType.User);

    /// <summary>All components that transitively call <paramref name="id"/> over synchronous flows.</summary>
    public IReadOnlySet<string> SyncDependents(string id)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var f in Inbound(cur))
            {
                if (!IsSync(f) || IsActor(From(f))) continue;
                if (seen.Add(f.From)) stack.Push(f.From);
            }
        }
        return seen;
    }

    /// <summary>Longest chain of synchronous hops starting at <paramref name="id"/>, as an ordered id list.</summary>
    public List<string> LongestSyncChainFrom(string id)
    {
        var best = new List<string> { id };
        Walk(id, new List<string> { id }, new HashSet<string> { id });
        return best;

        void Walk(string cur, List<string> path, HashSet<string> onPath)
        {
            foreach (var f in Outbound(cur))
            {
                if (!IsSync(f) || onPath.Contains(f.To)) continue;
                path.Add(f.To); onPath.Add(f.To);
                if (path.Count > best.Count) best = new List<string>(path);
                Walk(f.To, path, onPath);
                path.RemoveAt(path.Count - 1); onPath.Remove(f.To);
            }
        }
    }

    public string NameOf(string id) => ById.TryGetValue(id, out var c) ? c.Name : id;
    public string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(NameOf));
}
