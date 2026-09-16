using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecureFlow.Core.Model;

/// <summary>
/// A small, generic patch language so AI fixes can change the model without the fixer
/// knowing C# types. Applied through System.Text.Json so any property name works.
/// </summary>
public sealed class ModelPatch
{
    public List<PatchOp> Ops { get; set; } = new();
}

public sealed class PatchOp
{
    /// <summary>set-component-prop | set-flow-prop | add-component | add-flow | remove-flow</summary>
    public string Kind { get; set; } = "";
    /// <summary>Target component or flow id for set/remove ops.</summary>
    public string? Id { get; set; }
    /// <summary>Property name inside Component.Props or on Flow (camelCase or PascalCase).</summary>
    public string? Property { get; set; }
    public JsonElement? Value { get; set; }
    /// <summary>Full object for add-component / add-flow.</summary>
    public JsonElement? Object { get; set; }
}

public static class ModelPatcher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ArchitectureModel Apply(ArchitectureModel model, ModelPatch patch)
    {
        var root = JsonSerializer.SerializeToNode(model, Json)!.AsObject();
        var components = root["components"]!.AsArray();
        var flows = root["flows"]!.AsArray();

        foreach (var op in patch.Ops)
        {
            switch (op.Kind.ToLowerInvariant())
            {
                case "set-component-prop":
                {
                    var comp = Find(components, op.Id) ?? throw new InvalidOperationException($"Component '{op.Id}' not found");
                    var props = comp["props"]?.AsObject() ?? new JsonObject();
                    comp["props"] = props;
                    props[Camel(op.Property!)] = ToNode(op.Value);
                    break;
                }
                case "set-flow-prop":
                {
                    var flow = Find(flows, op.Id) ?? throw new InvalidOperationException($"Flow '{op.Id}' not found");
                    flow[Camel(op.Property!)] = ToNode(op.Value);
                    break;
                }
                case "add-component":
                    components.Add(ToNode(op.Object));
                    break;
                case "add-flow":
                    flows.Add(ToNode(op.Object));
                    break;
                case "remove-flow":
                {
                    var flow = Find(flows, op.Id);
                    if (flow is not null) flows.Remove(flow);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown patch op '{op.Kind}'");
            }
        }

        return root.Deserialize<ArchitectureModel>(Json)!;
    }

    private static JsonObject? Find(JsonArray arr, string? id) =>
        arr.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? ToNode(JsonElement? el) =>
        el is null || el.Value.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(el.Value.GetRawText());

    private static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
