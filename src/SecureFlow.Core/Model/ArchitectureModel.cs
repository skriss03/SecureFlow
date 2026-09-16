using System.Text.Json.Serialization;

namespace SecureFlow.Core.Model;

/// <summary>
/// Canonical architecture model. Every ingestion path (image, draw.io, repo) produces this,
/// every rule reads it, and every fix patches it. Nullable properties mean "unknown";
/// rules treat unknown as unproven and lower their confidence accordingly.
/// </summary>
public sealed class ArchitectureModel
{
    public string Name { get; set; } = "Untitled architecture";
    public string? Description { get; set; }
    public List<Component> Components { get; set; } = new();
    public List<Flow> Flows { get; set; } = new();
    public List<TrustBoundary> TrustBoundaries { get; set; } = new();
    /// <summary>Anything the extractor inferred rather than observed. Shown to the user.</summary>
    public List<string> Assumptions { get; set; } = new();

    public Component? FindComponent(string id) => Components.FirstOrDefault(c => c.Id == id);
    public Flow? FindFlow(string id) => Flows.FirstOrDefault(f => f.Id == id);
}

[JsonConverter(typeof(JsonStringEnumConverter<ComponentType>))]
public enum ComponentType
{
    User,          // human actor or browser / mobile client
    Gateway,       // API gateway, reverse proxy, WAF
    LoadBalancer,
    Cdn,
    WebApp,        // server-rendered or SPA host
    Api,           // service exposing endpoints
    Worker,        // background processor, consumer, cron
    Database,      // relational or document store
    Cache,
    Queue,         // queue, topic, event stream
    ObjectStorage, // blob / S3-style
    Identity,      // IdP, auth service
    Secrets,       // vault / key management
    Monitoring,    // logs, metrics, tracing
    External       // third-party dependency outside our control
}

public sealed class Component
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ComponentType Type { get; set; }
    /// <summary>Optional grouping such as "edge", "app", "data".</summary>
    public string? Tier { get; set; }
    public string? Description { get; set; }
    public ComponentProps Props { get; set; } = new();
    public List<Evidence> Evidence { get; set; } = new();
}

public sealed class ComponentProps
{
    public int? Replicas { get; set; }
    public int? Zones { get; set; }
    public int? Regions { get; set; }
    public bool? HasBackup { get; set; }
    public bool? HasHealthCheck { get; set; }
    public bool? Autoscale { get; set; }
    public bool? EncryptionAtRest { get; set; }
    public bool? PubliclyExposed { get; set; }
    /// <summary>True when a cloud provider operates it (managed DB, managed queue).</summary>
    public bool? Managed { get; set; }
    public bool? RateLimited { get; set; }
    public bool? AuditLogging { get; set; }
    public bool? InputValidation { get; set; }
    /// <summary>Set by repo extraction when credentials are found in source or config.</summary>
    public bool? HardcodedSecrets { get; set; }
    public string? Technology { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class Flow
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>http, https, grpc, amqp, sql, tcp, ...</summary>
    public string? Protocol { get; set; }
    /// <summary>none, apikey, basic, jwt, oauth, mtls, managed-identity, unknown</summary>
    public string? Auth { get; set; }
    /// <summary>True for request/response, false for message or event based.</summary>
    public bool? IsSync { get; set; }
    public int? TimeoutMs { get; set; }
    public int? Retries { get; set; }
    public bool? CircuitBreaker { get; set; }
    public bool? Encrypted { get; set; }
    public string? Description { get; set; }
    public List<Evidence> Evidence { get; set; } = new();
}

public sealed class TrustBoundary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ComponentIds { get; set; } = new();
}

public sealed class Evidence
{
    /// <summary>image, drawio, repo, manual, ai</summary>
    public string Source { get; set; } = "manual";
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string? Snippet { get; set; }

    public override string ToString() =>
        Path is null ? Source : Line is null ? Path : $"{Path}:{Line}";
}
