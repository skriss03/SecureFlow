using SecureFlow.Core.Model;

namespace SecureFlow.Core.Samples;

/// <summary>
/// Hand-built models used by tests, the frontend during development, and as a demo fallback.
/// "ShopFast" is a deliberately flawed e-commerce architecture that trips most rules.
/// </summary>
public static class SampleModels
{
    public static ArchitectureModel ShopFast()
    {
        Evidence Repo(string path, int line, string snippet) => new() { Source = "repo", Path = path, Line = line, Snippet = snippet };

        return new ArchitectureModel
        {
            Name = "ShopFast",
            Description = "E-commerce platform: storefront, orders, inventory, payments, notifications.",
            Components =
            {
                new() { Id = "user", Name = "Shopper (browser)", Type = ComponentType.User, Tier = "edge" },
                new() { Id = "cdn", Name = "CDN", Type = ComponentType.Cdn, Tier = "edge", Props = new() { Managed = true } },
                new() { Id = "gateway", Name = "API Gateway", Type = ComponentType.Gateway, Tier = "edge",
                    Props = new() { Replicas = 2, PubliclyExposed = true, RateLimited = false, HasHealthCheck = true, Autoscale = false, Technology = "nginx" },
                    Evidence = { Repo("deploy/k8s/gateway.yaml", 12, "replicas: 2") } },
                new() { Id = "web", Name = "Storefront Web", Type = ComponentType.WebApp, Tier = "app",
                    Props = new() { Replicas = 2, HasHealthCheck = true, Autoscale = true, Technology = "ASP.NET Core" },
                    Evidence = { Repo("src/Storefront/Storefront.csproj", 1, "<Project Sdk=\"Microsoft.NET.Sdk.Web\">") } },
                new() { Id = "orders-api", Name = "Orders API", Type = ComponentType.Api, Tier = "app",
                    Props = new() { Replicas = 1, HasHealthCheck = false, HardcodedSecrets = true, Technology = "ASP.NET Core" },
                    Evidence = { Repo("src/Orders/appsettings.json", 14, "\"ConnectionString\": \"Server=orders-db;User=sa;Password=P@ssw0rd!\"") } },
                new() { Id = "inventory-api", Name = "Inventory API", Type = ComponentType.Api, Tier = "app",
                    Props = new() { Replicas = 2, HasHealthCheck = true, Technology = "ASP.NET Core" },
                    Evidence = { Repo("src/Inventory/Program.cs", 22, "app.MapHealthChecks(\"/healthz\");") } },
                new() { Id = "notify-worker", Name = "Notification Worker", Type = ComponentType.Worker, Tier = "app",
                    Props = new() { Replicas = 1, Technology = ".NET Worker Service" },
                    Evidence = { Repo("src/Notifications/Worker.cs", 9, "public class Worker : BackgroundService") } },
                new() { Id = "orders-db", Name = "Orders DB (SQL)", Type = ComponentType.Database, Tier = "data",
                    Props = new() { Replicas = 1, Zones = 1, Regions = 1, HasBackup = false, EncryptionAtRest = false, Technology = "SQL Server" },
                    Evidence = { Repo("deploy/docker-compose.yml", 31, "image: mcr.microsoft.com/mssql/server:2022-latest") } },
                new() { Id = "cache", Name = "Session Cache (Redis)", Type = ComponentType.Cache, Tier = "data",
                    Props = new() { Replicas = 1, Zones = 1, Technology = "Redis" },
                    Evidence = { Repo("deploy/docker-compose.yml", 40, "image: redis:7") } },
                new() { Id = "payments", Name = "Payment Provider", Type = ComponentType.External, Tier = "external" },
                new() { Id = "email", Name = "Email Service", Type = ComponentType.External, Tier = "external" },
            },
            Flows =
            {
                new() { Id = "f1", From = "user", To = "cdn", Protocol = "https", Auth = "none", Encrypted = true, IsSync = true },
                new() { Id = "f2", From = "cdn", To = "gateway", Protocol = "https", Auth = "none", Encrypted = true, IsSync = true },
                new() { Id = "f3", From = "user", To = "gateway", Protocol = "https", Auth = "jwt", Encrypted = true, IsSync = true },
                new() { Id = "f4", From = "gateway", To = "web", Protocol = "http", Auth = "none", Encrypted = false, IsSync = true, TimeoutMs = 30000,
                    Evidence = { Repo("deploy/k8s/gateway.yaml", 27, "proxy_pass http://storefront:8080;") } },
                new() { Id = "f5", From = "web", To = "orders-api", Protocol = "http", Auth = "none", Encrypted = false, IsSync = true,
                    Evidence = { Repo("src/Storefront/Services/OrdersClient.cs", 18, "_http.BaseAddress = new Uri(\"http://orders-api\");") } },
                new() { Id = "f6", From = "web", To = "cache", Protocol = "tcp", Auth = "none", Encrypted = false, IsSync = true },
                new() { Id = "f7", From = "orders-api", To = "orders-db", Protocol = "sql", Auth = "basic", Encrypted = false, IsSync = true,
                    Evidence = { Repo("src/Orders/appsettings.json", 14, "\"ConnectionString\": \"Server=orders-db;...\"") } },
                new() { Id = "f8", From = "orders-api", To = "inventory-api", Protocol = "http", Auth = "none", Encrypted = false, IsSync = true,
                    Evidence = { Repo("src/Orders/Services/InventoryClient.cs", 21, "await _http.PostAsJsonAsync(\"/reserve\", lines);") } },
                new() { Id = "f9", From = "inventory-api", To = "orders-db", Protocol = "sql", Auth = "basic", Encrypted = false, IsSync = true },
                new() { Id = "f10", From = "orders-api", To = "payments", Protocol = "https", Auth = "apikey", Encrypted = true, IsSync = true, CircuitBreaker = false,
                    Evidence = { Repo("src/Orders/Services/PaymentClient.cs", 33, "var resp = await _http.PostAsync(\"/v1/charges\", body); // no Polly policy") } },
                new() { Id = "f11", From = "orders-api", To = "notify-worker", Protocol = "http", Auth = "none", Encrypted = false, IsSync = true,
                    Evidence = { Repo("src/Orders/Services/NotifyClient.cs", 12, "await _http.PostAsync(\"http://notify-worker/send\", payload);") } },
                new() { Id = "f12", From = "notify-worker", To = "email", Protocol = "https", Auth = "apikey", Encrypted = true, IsSync = true },
            },
            TrustBoundaries =
            {
                new() { Id = "tb-internet", Name = "Internet", ComponentIds = { "user", "payments", "email" } },
                new() { Id = "tb-edge", Name = "Edge / DMZ", ComponentIds = { "cdn", "gateway" } },
                new() { Id = "tb-private", Name = "Private network", ComponentIds = { "web", "orders-api", "inventory-api", "notify-worker", "orders-db", "cache" } },
            },
            Assumptions =
            {
                "Replica counts taken from deploy/k8s manifests; services without a manifest assumed single instance.",
                "No identity provider found in the repo; JWT validation assumed to happen at the gateway.",
            },
        };
    }

    /// <summary>A small, well-built model: every rule should stay quiet except the informational ones.</summary>
    public static ArchitectureModel Healthy()
    {
        return new ArchitectureModel
        {
            Name = "Healthy reference",
            Components =
            {
                new() { Id = "user", Name = "User", Type = ComponentType.User },
                new() { Id = "idp", Name = "Identity Provider", Type = ComponentType.Identity, Props = new() { Managed = true, AuditLogging = true } },
                new() { Id = "gw", Name = "Gateway", Type = ComponentType.Gateway, Props = new() { Replicas = 3, PubliclyExposed = true, RateLimited = true, HasHealthCheck = true, Autoscale = true, InputValidation = true } },
                new() { Id = "api", Name = "API", Type = ComponentType.Api, Props = new() { Replicas = 3, HasHealthCheck = true, Autoscale = true, InputValidation = true, AuditLogging = true, HardcodedSecrets = false } },
                new() { Id = "q", Name = "Queue", Type = ComponentType.Queue, Props = new() { Managed = true, Zones = 3 } },
                new() { Id = "worker", Name = "Worker", Type = ComponentType.Worker, Props = new() { Replicas = 2, HasHealthCheck = true } },
                new() { Id = "db", Name = "Database", Type = ComponentType.Database, Props = new() { Managed = true, Replicas = 2, Zones = 3, Regions = 2, HasBackup = true, EncryptionAtRest = true, AuditLogging = true } },
                new() { Id = "vault", Name = "Key Vault", Type = ComponentType.Secrets, Props = new() { Managed = true } },
                new() { Id = "mon", Name = "Monitoring", Type = ComponentType.Monitoring, Props = new() { Managed = true } },
            },
            Flows =
            {
                new() { Id = "f1", From = "user", To = "gw", Protocol = "https", Auth = "jwt", Encrypted = true, IsSync = true },
                new() { Id = "f2", From = "gw", To = "api", Protocol = "https", Auth = "mtls", Encrypted = true, IsSync = true, TimeoutMs = 5000, Retries = 2 },
                new() { Id = "f3", From = "api", To = "db", Protocol = "tls", Auth = "managed-identity", Encrypted = true, IsSync = true, TimeoutMs = 3000, Retries = 3 },
                new() { Id = "f4", From = "api", To = "q", Protocol = "amqps", Auth = "managed-identity", Encrypted = true, IsSync = false },
                new() { Id = "f5", From = "q", To = "worker", Protocol = "amqps", Auth = "managed-identity", Encrypted = true, IsSync = false },
                new() { Id = "f7", From = "api", To = "vault", Protocol = "https", Auth = "managed-identity", Encrypted = true, IsSync = true, TimeoutMs = 2000, Retries = 2 },
                new() { Id = "f8", From = "user", To = "idp", Protocol = "https", Auth = "oauth", Encrypted = true, IsSync = true },
            },
            TrustBoundaries =
            {
                new() { Id = "tb-internet", Name = "Internet", ComponentIds = { "user" } },
                new() { Id = "tb-cloud", Name = "Cloud VNet", ComponentIds = { "idp", "gw", "api", "q", "worker", "db", "vault", "mon" } },
            },
        };
    }
}
