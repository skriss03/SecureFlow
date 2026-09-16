using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SecureFlow.Core.Model;

namespace SecureFlow.Ingest;

/// <summary>
/// Deterministic draw.io (.drawio / .xml) parser. Needs no AI: shapes become components, edges become flows,
/// containers become trust boundaries. Types are inferred from the shape style and label.
/// </summary>
public static class DrawioIngest
{
    public static ArchitectureModel Parse(string xmlText, string? fileName = null)
    {
        var doc = XDocument.Parse(xmlText);
        var graph = doc.Descendants("mxGraphModel").FirstOrDefault();
        if (graph is null)
        {
            // Compressed diagram: <diagram> text is base64(deflate(urlencode(xml)))
            var diagram = doc.Descendants("diagram").FirstOrDefault()
                ?? throw new InvalidOperationException("Not a draw.io file: no <mxGraphModel> or <diagram> element.");
            var inner = Decompress(diagram.Value.Trim());
            graph = XDocument.Parse(inner).Descendants("mxGraphModel").FirstOrDefault()
                ?? throw new InvalidOperationException("Could not decode the draw.io diagram.");
        }

        var cells = graph.Descendants("mxCell").ToList();
        var byId = cells.ToDictionary(c => (string?)c.Attribute("id") ?? "", c => c);

        // Labels may live on a wrapping <object label="..."><mxCell .../></object>
        string LabelOf(XElement cell)
        {
            var v = (string?)cell.Attribute("value");
            if (string.IsNullOrWhiteSpace(v) && cell.Parent?.Name == "object")
                v = (string?)cell.Parent.Attribute("label");
            return StripHtml(v ?? "");
        }

        var model = new ArchitectureModel
        {
            Name = Path.GetFileNameWithoutExtension(fileName ?? "") is { Length: > 0 } n ? n : "draw.io diagram",
        };

        var vertices = cells.Where(c => (string?)c.Attribute("vertex") == "1").ToList();
        var edges = cells.Where(c => (string?)c.Attribute("edge") == "1").ToList();
        var edgeIds = edges.Select(e => (string?)e.Attribute("id") ?? "").ToHashSet();

        // Containers = vertices that have other vertices as children (or swimlane/group styles).
        var childCount = vertices.GroupBy(v => (string?)v.Attribute("parent") ?? "").ToDictionary(g => g.Key, g => g.Count());
        bool IsContainer(XElement v)
        {
            var id = (string?)v.Attribute("id") ?? "";
            var style = ((string?)v.Attribute("style") ?? "").ToLowerInvariant();
            return childCount.ContainsKey(id) || style.Contains("swimlane") || style.Contains("group") || style.Contains("container=1");
        }

        var componentCells = vertices.Where(v => !IsContainer(v) && !edgeIds.Contains((string?)v.Attribute("parent") ?? "") && LabelOf(v).Length > 0).ToList();
        var idMap = new Dictionary<string, string>();
        var usedIds = new HashSet<string>();

        foreach (var v in componentCells)
        {
            var label = LabelOf(v);
            var style = (string?)v.Attribute("style") ?? "";
            var slug = Slug(label);
            var id = slug; var k = 2;
            while (!usedIds.Add(id)) id = $"{slug}-{k++}";
            idMap[(string?)v.Attribute("id") ?? ""] = id;
            var (type, tech) = InferType(style, label);
            var comp = new Component
            {
                Id = id,
                Name = label,
                Type = type,
                Props = new ComponentProps { Technology = tech },
                Evidence = { new Evidence { Source = "drawio", Snippet = label } },
            };
            ApplyAnnotations(comp, label);
            model.Components.Add(comp);
        }

        var n2 = 0;
        foreach (var e in edges)
        {
            var src = (string?)e.Attribute("source"); var tgt = (string?)e.Attribute("target");
            if (src is null || tgt is null || !idMap.TryGetValue(src, out var from) || !idMap.TryGetValue(tgt, out var to) || from == to) continue;
            var label = LabelOf(e);
            // edge labels are often child cells whose parent is the edge
            var eid = (string?)e.Attribute("id") ?? "";
            var childLabels = vertices.Where(v => (string?)v.Attribute("parent") == eid).Select(LabelOf).Where(s => s.Length > 0);
            label = string.Join(" ", new[] { label }.Concat(childLabels)).Trim();
            var style = ((string?)e.Attribute("style") ?? "").ToLowerInvariant();

            var flow = new Flow { Id = $"f{++n2}", From = from, To = to, Description = label.Length > 0 ? label : null };
            if (label.Length > 0) flow.Evidence.Add(new Evidence { Source = "drawio", Snippet = label });
            ApplyFlowAnnotations(flow, label, style, model.FindComponent(to)!);
            model.Flows.Add(flow);
        }

        // Trust boundaries from containers: every component whose parent chain includes the container.
        foreach (var container in vertices.Where(IsContainer))
        {
            var cid = (string?)container.Attribute("id") ?? "";
            var members = componentCells.Where(v => ParentChain(v, byId).Contains(cid)).Select(v => idMap[(string?)v.Attribute("id") ?? ""]).ToList();
            if (members.Count == 0) continue;
            var name = LabelOf(container);
            if (name.Length == 0) name = "Boundary " + (model.TrustBoundaries.Count + 1);
            model.TrustBoundaries.Add(new TrustBoundary { Id = Slug(name), Name = name, ComponentIds = members });
        }

        model.Assumptions.Add("Model parsed deterministically from draw.io shapes; component types inferred from shape styles and labels.");
        if (model.Flows.Any(f => f.Protocol is null))
            model.Assumptions.Add("Edges without a protocol label are assumed synchronous; protocol, auth and encryption unknown.");
        return model;
    }

    private static string Decompress(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var input = new MemoryStream(bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return Uri.UnescapeDataString(reader.ReadToEnd());
    }

    private static IEnumerable<string> ParentChain(XElement cell, Dictionary<string, XElement> byId)
    {
        var p = (string?)cell.Attribute("parent");
        var guard = 0;
        while (p is not null && byId.TryGetValue(p, out var parent) && guard++ < 50)
        {
            yield return p;
            p = (string?)parent.Attribute("parent");
        }
    }

    private static (ComponentType, string?) InferType(string style, string label)
    {
        var s = style.ToLowerInvariant();
        var l = label.ToLowerInvariant();
        string? tech = null;
        foreach (var t in new[] { "redis", "postgres", "mysql", "sql server", "cosmos", "mongo", "kafka", "rabbitmq", "service bus", "nginx", "kubernetes", "aks", "eks", "lambda", "azure functions", "app service", "dynamodb", "s3", "blob" })
            if (l.Contains(t) || s.Contains(t.Replace(" ", "_")) || s.Contains(t.Replace(" ", ""))) { tech = t; break; }

        bool Has(params string[] keys) => keys.Any(k => s.Contains(k) || l.Contains(k));

        if (Has("actor", "user", "customer", "browser", "mobile", "client", "shopper", "person")) return (ComponentType.User, tech);
        if (Has("api management", "apim", "gateway", "waf", "front door", "ingress", "reverse proxy", "nginx", "envoy", "kong")) return (ComponentType.Gateway, tech);
        if (Has("load balancer", "loadbalancer", "alb", "elb", "traffic manager")) return (ComponentType.LoadBalancer, tech);
        if (Has("cdn", "cloudfront", "akamai")) return (ComponentType.Cdn, tech);
        if (Has("queue", "topic", "service bus", "servicebus", "event hub", "eventhub", "kafka", "rabbit", "sqs", "sns", "pub/sub", "pubsub", "event grid")) return (ComponentType.Queue, tech);
        if (Has("redis", "cache", "memcached")) return (ComponentType.Cache, tech);
        if (Has("blob", "bucket", "s3", "object storage", "storage account", "file share")) return (ComponentType.ObjectStorage, tech);
        if (Has("database", "cylinder", "sql", "postgres", "mysql", "cosmos", "mongo", "dynamo", "db", "datastore")) return (ComponentType.Database, tech);
        if (Has("key vault", "keyvault", "vault", "kms", "secrets", "secret manager")) return (ComponentType.Secrets, tech);
        if (Has("identity", "entra", "azure ad", "active directory", "idp", "auth0", "okta", "cognito", "keycloak", "b2c")) return (ComponentType.Identity, tech);
        if (Has("monitor", "insights", "logging", "grafana", "prometheus", "datadog", "splunk", "log analytics", "telemetry")) return (ComponentType.Monitoring, tech);
        if (Has("worker", "function", "lambda", "job", "cron", "consumer", "processor", "batch")) return (ComponentType.Worker, tech);
        if (Has("cloud", "external", "third", "3rd", "partner", "stripe", "paypal", "sendgrid", "twilio", "provider", "saas")) return (ComponentType.External, tech);
        if (Has("web", "frontend", "spa", "portal", "site", "ui", "storefront")) return (ComponentType.WebApp, tech);
        return (ComponentType.Api, tech);
    }

    private static readonly Regex Replicas = new(@"(?:x|×)\s*(\d+)|(\d+)\s*(?:replicas?|instances?|nodes?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ApplyAnnotations(Component c, string label)
    {
        var l = label.ToLowerInvariant();
        var m = Replicas.Match(label);
        if (m.Success) c.Props.Replicas = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        if (l.Contains("multi-az") || l.Contains("zone redundant") || l.Contains("zone-redundant") || l.Contains("multi az")) c.Props.Zones = 3;
        if (l.Contains("geo") || l.Contains("multi-region")) c.Props.Regions = 2;
        if (l.Contains("backup")) c.Props.HasBackup = true;
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
        else if (l.Contains("sql") || l.Contains("tds")) { f.Protocol = "sql"; }
        else if (l.Contains("tls")) { f.Encrypted = true; }

        if (l.Contains("mtls")) { f.Auth = "mtls"; f.Encrypted = true; }
        else if (l.Contains("jwt") || l.Contains("bearer")) f.Auth = "jwt";
        else if (l.Contains("oauth") || l.Contains("oidc")) f.Auth = "oauth";
        else if (l.Contains("api key") || l.Contains("apikey")) f.Auth = "apikey";
        else if (l.Contains("managed identity") || l.Contains("msi")) f.Auth = "managed-identity";
        else if (l.Contains("anonymous") || l.Contains("no auth")) f.Auth = "none";

        if (l.Contains("async") || l.Contains("event") || l.Contains("publish") || l.Contains("subscribe") || style.Contains("dashed=1") || target.Type == ComponentType.Queue)
            f.IsSync = false;
        if (l.Contains("retry")) f.Retries = 3;
        if (l.Contains("circuit")) f.CircuitBreaker = true;
        var t = Regex.Match(l, @"(\d+)\s*(ms|s)\s*timeout|timeout\s*[:=]?\s*(\d+)\s*(ms|s)?");
        if (t.Success)
        {
            var num = int.Parse(t.Groups[1].Success ? t.Groups[1].Value : t.Groups[3].Value);
            var unit = t.Groups[2].Success ? t.Groups[2].Value : t.Groups[4].Value;
            f.TimeoutMs = unit == "s" ? num * 1000 : num;
        }
    }

    private static string StripHtml(string s)
    {
        var text = Regex.Replace(s, "<br\\s*/?>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return slug.Length == 0 ? "node" : slug;
    }
}
