using System.Text;
using System.Text.RegularExpressions;
using SecureFlow.Core.Model;

namespace SecureFlow.Ingest.Diagrams;

/// <summary>
/// Turns a tool-neutral <see cref="DiagramGraph"/> into the architecture model. This is the single place where
/// diagram shapes get architecture meaning (component type from the shape and label, replicas and zones from
/// annotations, protocol/auth/async from arrow labels), so every diagram format is analysed identically.
/// Deterministic: no AI.
/// </summary>
public static class DiagramModelBuilder
{
    private static readonly Dictionary<string, string> FormatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drawio"] = "draw.io", ["mermaid"] = "Mermaid", ["plantuml"] = "PlantUML", ["structurizr"] = "Structurizr DSL",
        ["visio"] = "Visio", ["excalidraw"] = "Excalidraw", ["gliffy"] = "Gliffy",
        ["image"] = "Image", ["pdf"] = "PDF", ["svg"] = "SVG", ["text"] = "Text",
    };

    public static string DisplayName(string format) => FormatNames.TryGetValue(format, out var n) ? n : format;

    /// <param name="extraKeywords">Additional words per component type (from configuration), checked before the built-in ones.</param>
    public static ArchitectureModel Build(DiagramGraph graph, IReadOnlyDictionary<ComponentType, string[]>? extraKeywords = null)
    {
        var model = new ArchitectureModel
        {
            Name = !string.IsNullOrWhiteSpace(graph.Name) ? graph.Name!
                : Path.GetFileNameWithoutExtension(StripDoubleExtension(graph.FileName ?? "")) is { Length: > 0 } n ? n
                : $"{DisplayName(graph.Format)} diagram",
        };

        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var containerIds = graph.Nodes.Where(n => n.IsContainer).Select(n => n.Id).ToHashSet();
        var componentNodes = graph.Nodes.Where(n => !n.IsContainer && n.Label.Trim().Length > 0).ToList();

        var idMap = new Dictionary<string, string>();
        var usedIds = new HashSet<string>();
        foreach (var node in componentNodes)
        {
            var label = node.Label.Trim();
            var (cleanName, parenthesized) = CleanLabel(label);
            var slug = Slug(cleanName);
            var id = slug; var k = 2;
            while (!usedIds.Add(id)) id = $"{slug}-{k++}";
            idMap[node.Id] = id;

            var annotationText = string.Join(" ", new[] { label, node.Description, node.Technology }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var (type, tech) = InferType(node.TypeHint ?? "", annotationText, extraKeywords);
            var comp = new Component
            {
                Id = id,
                Name = cleanName,
                Type = type,
                Props = new ComponentProps { Technology = node.Technology ?? tech ?? parenthesized },
                Evidence = { Evidence(graph, node.Line, label) },
            };
            ApplyAnnotations(comp, annotationText);
            model.Components.Add(comp);
        }

        var skippedEdges = 0;
        var n2 = 0;
        foreach (var e in graph.Edges)
        {
            if (!idMap.TryGetValue(e.SourceId, out var from) || !idMap.TryGetValue(e.TargetId, out var to) || from == to)
            {
                skippedEdges++;
                continue;
            }
            var label = e.Label.Trim();
            var flow = new Flow { Id = $"f{++n2}", From = from, To = to, Description = label.Length > 0 ? label : null };
            if (label.Length > 0 || graph.HasLineNumbers) flow.Evidence.Add(Evidence(graph, e.Line, label.Length > 0 ? label : $"{e.SourceId} -> {e.TargetId}"));
            var style = (e.Style ?? "").ToLowerInvariant() + (e.Dashed ? " dashed=1" : "");
            ApplyFlowAnnotations(flow, label, style, model.FindComponent(to)!);
            model.Flows.Add(flow);
        }

        // Trust boundaries: every component whose parent chain passes through the container.
        var usedBoundaryIds = new HashSet<string>();
        foreach (var container in graph.Nodes.Where(n => n.IsContainer))
        {
            var members = componentNodes.Where(n => ParentChain(n, byId).Contains(container.Id)).Select(n => idMap[n.Id]).ToList();
            if (members.Count == 0) continue;
            var name = container.Label.Trim();
            if (name.Length == 0) name = "Boundary " + (model.TrustBoundaries.Count + 1);
            var bid = Slug(name); var k = 2;
            while (!usedBoundaryIds.Add(bid)) bid = $"{Slug(name)}-{k++}";
            model.TrustBoundaries.Add(new TrustBoundary { Id = bid, Name = name, ComponentIds = members });
        }

        // Wording kept exactly as the original draw.io importer had it: models (and the AI cache keyed on them) stay identical.
        model.Assumptions.Add($"Model parsed deterministically from {DisplayName(graph.Format)} shapes; component types inferred from shape styles and labels.");
        if (model.Flows.Any(f => f.Protocol is null))
            model.Assumptions.Add("Edges without a protocol label are assumed synchronous; protocol, auth and encryption unknown.");
        if (skippedEdges > 0)
            model.Assumptions.Add($"{skippedEdges} connector(s) were not attached to two labelled shapes and were ignored.");
        model.Assumptions.AddRange(graph.Notes);
        return model;
    }

    // Path and line only for text formats, where "architecture.mmd:12" points somewhere useful. Shape-based
    // formats cite the label; this also keeps draw.io models byte-identical to before (the AI replay cache keys on them).
    private static Evidence Evidence(DiagramGraph g, int? line, string snippet) => new()
    {
        Source = g.Format,
        Path = g.HasLineNumbers ? g.FileName : null,
        Line = g.HasLineNumbers ? line : null,
        Snippet = snippet,
    };

    private static string StripDoubleExtension(string fileName) =>
        Regex.Replace(fileName, @"\.drawio(?=\.(svg|png|xml)$)", "", RegexOptions.IgnoreCase);

    private static IEnumerable<string> ParentChain(DiagramNode node, Dictionary<string, DiagramNode> byId)
    {
        var p = node.ParentId;
        var guard = 0;
        while (p is not null && byId.TryGetValue(p, out var parent) && guard++ < 50)
        {
            yield return p;
            p = parent.ParentId;
        }
    }

    internal static (ComponentType, string?) InferType(string style, string label, IReadOnlyDictionary<ComponentType, string[]>? extra = null)
    {
        var s = style.ToLowerInvariant();
        var l = label.ToLowerInvariant();
        string? tech = null;
        foreach (var t in new[] { "redis", "postgres", "mysql", "sql server", "oracle", "cosmos", "mongo", "kafka", "rabbitmq", "service bus", "nginx", "kubernetes", "aks", "eks", "lambda", "azure functions", "app service", "dynamodb", "s3", "blob" })
            if (l.Contains(t) || s.Contains(t.Replace(" ", "_")) || s.Contains(t.Replace(" ", ""))) { tech = t; break; }

        bool Has(params string[] keys) => keys.Any(k => s.Contains(k) || l.Contains(k));

        if (extra is not null)
            foreach (var (type, words) in extra)
                if (words.Length > 0 && Has(words.Select(w => w.ToLowerInvariant()).ToArray())) return (type, tech);

        if (Has("actor", "user", "customer", "browser", "mobile", "client", "shopper", "person")) return (ComponentType.User, tech);
        if (Has("api management", "apim", "gateway", "waf", "front door", "ingress", "reverse proxy", "nginx", "envoy", "kong")) return (ComponentType.Gateway, tech);
        if (Has("load balancer", "loadbalancer", "alb", "elb", "traffic manager")) return (ComponentType.LoadBalancer, tech);
        if (Has("cdn", "cloudfront", "akamai")) return (ComponentType.Cdn, tech);
        if (Has("queue", "topic", "service bus", "servicebus", "event hub", "eventhub", "kafka", "rabbit", "sqs", "sns", "pub/sub", "pubsub", "event grid")) return (ComponentType.Queue, tech);
        if (Has("redis", "cache", "memcached")) return (ComponentType.Cache, tech);
        if (Has("blob", "bucket", "s3", "object storage", "storage account", "file share")) return (ComponentType.ObjectStorage, tech);
        if (Has("database", "cylinder", "sql", "postgres", "mysql", "oracle", "cosmos", "mongo", "dynamo", "db", "datastore")) return (ComponentType.Database, tech);
        if (Has("key vault", "keyvault", "vault", "kms", "secrets", "secret manager")) return (ComponentType.Secrets, tech);
        if (Has("identity", "entra", "azure ad", "active directory", "idp", "auth0", "okta", "cognito", "keycloak", "b2c")) return (ComponentType.Identity, tech);
        if (Has("monitor", "insights", "logging", "grafana", "prometheus", "datadog", "splunk", "log analytics", "telemetry")) return (ComponentType.Monitoring, tech);
        if (Has("worker", "function", "lambda", "job", "cron", "consumer", "processor", "batch")) return (ComponentType.Worker, tech);
        if (Has("cloud", "external", "third", "3rd", "partner", "stripe", "paypal", "sendgrid", "twilio", "provider", "saas", "card network", "visa", "mastercard")) return (ComponentType.External, tech);
        // "spa", "ui" and "site" are matched in the label only: as substrings they occur in ordinary style text
        // (draw.io's default "whiteSpace=wrap" contains "spa"), which would turn every API box into a web app.
        if (Has("web", "frontend", "portal", "storefront") || new[] { "spa", "ui", "site" }.Any(k => System.Text.RegularExpressions.Regex.IsMatch(l, $@"\b{k}\b")))
            return (ComponentType.WebApp, tech);
        return (ComponentType.Api, tech);
    }

    private static readonly Regex Replicas = new(@"(?:\bx|×)\s*(\d+)\b|\b(\d+)\s*(?:replicas?|instances?|nodes?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnnotationWords = new(
        @"\b(?:x|×)\s*\d+\b|\b\d+\s*(?:replicas?|instances?|nodes?)\b|\bautoscal\w*\b|\bsingle[ -]zone\b|\bmulti[ -]az\b|\bzone[ -]redundant\b|\bgeo(?:[ -]replicated)?\b|\bmulti[ -]region\b|\bbackups?\b|\bmanaged\b|\bpublic\b|\bprivate\b|\bHA\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"API Gateway (nginx) x2" → ("API Gateway", "nginx"). Annotations stay available for props via <see cref="ApplyAnnotations"/>.</summary>
    internal static (string Name, string? Parenthesized) CleanLabel(string label)
    {
        string? paren = null;
        var m = Regex.Match(label, @"\(([^)]+)\)");
        if (m.Success) paren = m.Groups[1].Value.Trim();
        var name = Regex.Replace(label, @"\([^)]*\)", " ");
        name = AnnotationWords.Replace(name, " ");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', ',', '-', ':');
        if (name.Length == 0) name = label;
        return (name, paren);
    }

    private static void ApplyAnnotations(Component c, string label)
    {
        var l = label.ToLowerInvariant();
        var m = Replicas.Match(label);
        if (m.Success) c.Props.Replicas = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        if (l.Contains("multi-az") || l.Contains("zone redundant") || l.Contains("zone-redundant") || l.Contains("multi az")) c.Props.Zones = 3;
        if (l.Contains("geo") || l.Contains("multi-region")) c.Props.Regions = 2;
        if (Regex.IsMatch(l, @"\bno backups?\b")) c.Props.HasBackup = false;
        else if (l.Contains("backup")) c.Props.HasBackup = true;
        if (l.Contains("autoscal")) c.Props.Autoscale = true;
        if (l.Contains("managed")) c.Props.Managed = true;
        if (l.Contains("public")) c.Props.PubliclyExposed = true;
        if (l.Contains("private")) c.Props.PubliclyExposed = false;
    }

    private static void ApplyFlowAnnotations(Flow f, string label, string style, Component target)
    {
        var l = label.ToLowerInvariant();
        if (l.Contains("https")) { f.Protocol = "https"; f.Encrypted = true; }
        else if (l.Contains("http")) { f.Protocol = "http"; f.Encrypted = false; }
        else if (l.Contains("grpc")) { f.Protocol = "grpc"; }
        else if (l.Contains("amqps")) { f.Protocol = "amqps"; f.Encrypted = true; }
        else if (l.Contains("amqp")) { f.Protocol = "amqp"; }
        else if (l.Contains("sql") || l.Contains("tds") || l.Contains("jdbc")) { f.Protocol = "sql"; }
        else if (l.Contains("tls")) { f.Encrypted = true; }

        if (l.Contains("mtls")) { f.Auth = "mtls"; f.Encrypted = true; }
        else if (l.Contains("jwt") || l.Contains("bearer")) f.Auth = "jwt";
        else if (l.Contains("oauth") || l.Contains("oidc")) f.Auth = "oauth";
        else if (l.Contains("api key") || l.Contains("apikey")) f.Auth = "apikey";
        else if (l.Contains("managed identity") || l.Contains("msi")) f.Auth = "managed-identity";
        else if (l.Contains("anonymous") || l.Contains("no auth") || l.Contains("unauthenticated")) f.Auth = "none";

        if (l.Contains("async") || l.Contains("event") || l.Contains("publish") || l.Contains("subscribe") || style.Contains("dashed=1") || target.Type == ComponentType.Queue)
            f.IsSync = false;
        else if (l.Contains("sync")) f.IsSync = true;
        if (l.Contains("retry")) f.Retries = 3;
        if (l.Contains("circuit")) f.CircuitBreaker = true;
        if (l.Contains("no timeout")) f.TimeoutMs = null;
        var t = Regex.Match(l, @"(\d+)\s*(ms|s)\s*timeout|timeout\s*[:=]?\s*(\d+)\s*(ms|s)?");
        if (t.Success)
        {
            var num = int.Parse(t.Groups[1].Success ? t.Groups[1].Value : t.Groups[3].Value);
            var unit = t.Groups[2].Success ? t.Groups[2].Value : t.Groups[4].Value;
            f.TimeoutMs = unit == "s" ? num * 1000 : num;
        }
    }

    internal static string StripHtml(string s)
    {
        var text = Regex.Replace(s, "<br\\s*/?>|</p>|</div>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    internal static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return slug.Length == 0 ? "node" : slug;
    }
}
