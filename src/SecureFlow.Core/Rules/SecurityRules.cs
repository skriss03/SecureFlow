using SecureFlow.Core.Model;
using static SecureFlow.Core.Rules.ModelGraph;

namespace SecureFlow.Core.Rules;

public sealed class UnauthenticatedCrossBoundaryFlow : Rule
{
    public override string Id => "S01";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Spoofing";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Unauthenticated call across a trust boundary";
    public override string Rationale => "Any caller that reaches the endpoint can impersonate a legitimate one. Trust boundaries exist precisely because the other side cannot be assumed friendly.";
    public override string Mitigation => "Require authentication on the receiving side: OAuth/JWT for user traffic, mTLS or managed identity for service-to-service traffic. Deny by default.";
    public override string? FixHint => "set-flow-prop auth = jwt | mtls | managed-identity";

    private static bool IsAnonymous(string? auth) => auth is null || auth.Equals("none", StringComparison.OrdinalIgnoreCase) || auth.Equals("unknown", StringComparison.OrdinalIgnoreCase) || auth.Equals("anonymous", StringComparison.OrdinalIgnoreCase);

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            if (!g.CrossesBoundary(f) || !IsAnonymous(f.Auth)) continue;
            var from = g.From(f); var to = g.To(f);
            if (to.Type is ComponentType.User or ComponentType.Cdn) continue;
            var sev = from.Type == ComponentType.User ? Severity.Medium : Severity.High;
            yield return new RuleMatch($"{from.Name} → {to.Name} crosses a trust boundary without authentication.", Ids(from.Id, to.Id), Ids(f.Id), f.Auth is null ? Confidence.Medium : Confidence.High, sev);
        }
    }
}

public sealed class UnencryptedFlow : Rule
{
    public override string Id => "S02";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Information disclosure";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Data in transit is not encrypted";
    public override string Rationale => "Cleartext traffic can be read and modified by anyone on the path: a compromised host, a misconfigured proxy, or a cloud-provider insider. Credentials and PII are the usual casualties.";
    public override string Mitigation => "Use TLS everywhere, including inside the private network. Prefer mTLS for service-to-service calls and enforce TLS on database and queue connections.";
    public override string? FixHint => "set-flow-prop encrypted = true; set-flow-prop protocol = https";

    private static readonly string[] SecureProtocols = { "https", "grpcs", "tls", "mtls", "amqps", "wss", "ssh", "sftp" };
    private static readonly string[] ClearProtocols = { "http", "tcp", "amqp", "ws", "ftp", "smtp" };

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            if (f.Encrypted == true) continue;
            var proto = f.Protocol?.ToLowerInvariant();
            if (proto is not null && SecureProtocols.Contains(proto)) continue;
            if (string.Equals(f.Auth, "mtls", StringComparison.OrdinalIgnoreCase)) continue;
            var from = g.From(f); var to = g.To(f);

            if (f.Encrypted == false || (proto is not null && ClearProtocols.Contains(proto)))
            {
                yield return new RuleMatch($"{from.Name} → {to.Name} uses {proto ?? "an unencrypted channel"}.", Ids(from.Id, to.Id), Ids(f.Id), Confidence.High, g.CrossesBoundary(f) ? Severity.High : Severity.Medium);
            }
            else if (g.CrossesBoundary(f))
            {
                yield return new RuleMatch($"{from.Name} → {to.Name} crosses a trust boundary; encryption is not confirmed.", Ids(from.Id, to.Id), Ids(f.Id), Confidence.Low, Severity.Medium);
            }
        }
    }
}

public sealed class HardcodedSecrets : Rule
{
    public override string Id => "S03";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Information disclosure";
    public override Severity DefaultSeverity => Severity.Critical;
    public override string Title => "Credentials committed in source or configuration";
    public override string Rationale => "Anything in the repository is visible to every developer, every CI runner, every fork and every leaked laptop. Secrets in config are the most common root cause of cloud account compromise.";
    public override string Mitigation => "Move secrets to a vault or the platform's secret store, inject them at runtime, rotate the exposed values now, and add a secret scanner to CI.";
    public override string? FixHint => "set-component-prop hardcodedSecrets = false; add-component secrets vault; add-flow service→vault";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Props.HardcodedSecrets != true) continue;
            yield return new RuleMatch($"{c.Name} has credentials in its source or configuration files.", Ids(c.Id), None);
        }
    }
}

public sealed class NoAuditLogging : Rule
{
    public override string Id => "S04";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Repudiation";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "No audit trail on sensitive operations";
    public override string Rationale => "Without an immutable record of who did what, you cannot investigate an incident, prove compliance, or dispute a fraudulent action.";
    public override string Mitigation => "Emit audit events for authentication, authorization decisions and data writes to a write-once store separate from application logs.";
    public override string? FixHint => "set-component-prop auditLogging = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            var relevant = c.Type is ComponentType.Database or ComponentType.Identity || (c.Type == ComponentType.Api && g.IsPublic(c));
            if (!relevant || c.Props.AuditLogging == true) continue;
            yield return new RuleMatch($"{c.Name} has no audit logging.", Ids(c.Id), None, Conf(c.Props.AuditLogging));
        }
    }
}

public sealed class PubliclyExposedDataStore : Rule
{
    public override string Id => "S05";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Information disclosure";
    public override Severity DefaultSeverity => Severity.Critical;
    public override string Title => "Data store reachable from the internet";
    public override string Rationale => "Internet-exposed databases and buckets are scanned within minutes. One weak credential or misconfigured ACL exposes the entire data set.";
    public override string Mitigation => "Move the store into a private network, restrict access to application identities, and disable public endpoints.";
    public override string? FixHint => "set-component-prop publiclyExposed = false";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsDataStore(c) || c.Props.PubliclyExposed != true) continue;
            yield return new RuleMatch($"{c.Name} is publicly exposed.", Ids(c.Id), None);
        }
    }
}

public sealed class NoInputValidationAtEdge : Rule
{
    public override string Id => "S06";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Tampering";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Untrusted input not validated at the edge";
    public override string Rationale => "Every injection class (SQL, command, template, deserialization) starts with input that was trusted too early. Validation at the boundary is the cheapest control you have.";
    public override string Mitigation => "Validate and canonicalize input against a schema at the first trusted component, reject on failure, and use parameterized queries downstream.";
    public override string? FixHint => "set-component-prop inputValidation = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (!IsService(c) || c.Props.InputValidation == true) continue;
            var untrusted = g.Inbound(c.Id).Where(f => IsActor(g.From(f))).ToList();
            if (untrusted.Count == 0) continue;
            yield return new RuleMatch($"{c.Name} receives input from {g.Names(untrusted.Select(f => f.From).Distinct())} without confirmed validation.", Ids(c.Id), untrusted.Select(f => f.Id).ToList(), Conf(c.Props.InputValidation));
        }
    }
}

public sealed class NoRateLimiting : Rule
{
    public override string Id => "S07";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Denial of service";
    public override Severity DefaultSeverity => Severity.High;
    public override string Title => "Public endpoint without rate limiting";
    public override string Rationale => "Without a request budget per client, one script can consume all capacity, run credential stuffing at full speed, or run up your cloud bill.";
    public override string Mitigation => "Enforce per-client rate limits and quotas at the gateway, with stricter limits on authentication and expensive endpoints.";
    public override string? FixHint => "set-component-prop rateLimited = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type is not (ComponentType.Gateway or ComponentType.Api or ComponentType.WebApp)) continue;
            if (!g.IsPublic(c) || c.Props.RateLimited == true) continue;
            // If a gateway in front already rate limits, the service behind it is covered.
            var coveredByGateway = g.Inbound(c.Id).Any(f => g.From(f).Type == ComponentType.Gateway && g.From(f).Props.RateLimited == true);
            if (coveredByGateway) continue;
            yield return new RuleMatch($"{c.Name} is publicly reachable with no rate limiting.", Ids(c.Id), None, Conf(c.Props.RateLimited));
        }
    }
}

public sealed class NoSecretsManager : Rule
{
    public override string Id => "S08";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Elevation of privilege";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "No secrets manager in the architecture";
    public override string Rationale => "Services need credentials for databases and third parties. Without a vault or managed identities those credentials end up in config files, environment variables and CI logs.";
    public override string Mitigation => "Introduce a secrets manager or managed identities so no service holds long-lived credentials, and rotate automatically.";
    public override string? FixHint => "add-component secrets vault; set flows auth = managed-identity";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        if (g.Model.Components.Any(c => c.Type == ComponentType.Secrets)) yield break;
        if (g.Model.Flows.Any(f => string.Equals(f.Auth, "managed-identity", StringComparison.OrdinalIgnoreCase))) yield break;
        var needs = g.Model.Components.Where(c => c.Type is ComponentType.Database or ComponentType.External or ComponentType.Queue).ToList();
        if (needs.Count == 0) yield break;
        yield return new RuleMatch($"Services authenticate to {g.Names(needs.Select(c => c.Id))} but no vault or managed identity is modeled.", needs.Select(c => c.Id).ToList(), None, Confidence.Medium);
    }
}

public sealed class NoEncryptionAtRest : Rule
{
    public override string Id => "S09";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Information disclosure";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Data at rest is not encrypted";
    public override string Rationale => "Disk snapshots, backups and decommissioned drives leave the security perimeter. Encryption at rest is the control that makes those leaks harmless.";
    public override string Mitigation => "Enable encryption at rest with customer-managed keys where regulation requires it, and include backups and snapshots.";
    public override string? FixHint => "set-component-prop encryptionAtRest = true";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type is not (ComponentType.Database or ComponentType.ObjectStorage)) continue;
            if (c.Props.EncryptionAtRest == true) continue;
            yield return new RuleMatch($"{c.Name} is not confirmed to be encrypted at rest.", Ids(c.Id), None, Conf(c.Props.EncryptionAtRest));
        }
    }
}

public sealed class DirectDataAccessFromUntrusted : Rule
{
    public override string Id => "S10";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Elevation of privilege";
    public override Severity DefaultSeverity => Severity.Critical;
    public override string Title => "Untrusted actor talks directly to a data store";
    public override string Rationale => "A client or third party with a direct database or storage connection bypasses every authorization rule in the application layer.";
    public override string Mitigation => "Front the data store with an API that enforces authentication and authorization, and revoke direct network access.";
    public override string? FixHint => "remove-flow actor→datastore; add-flow actor→api; add-flow api→datastore";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var f in g.Model.Flows)
        {
            if (!g.ById.ContainsKey(f.From) || !g.ById.ContainsKey(f.To)) continue;
            var from = g.From(f); var to = g.To(f);
            if (!IsActor(from) || !IsDataStore(to)) continue;
            yield return new RuleMatch($"{from.Name} accesses {to.Name} directly.", Ids(from.Id, to.Id), Ids(f.Id));
        }
    }
}

public sealed class NoEdgeProtection : Rule
{
    public override string Id => "S11";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Denial of service";
    public override Severity DefaultSeverity => Severity.Medium;
    public override string Title => "Application exposed without a gateway or WAF";
    public override string Rationale => "A gateway, WAF or CDN is where TLS termination, bot filtering, rate limits and request validation live. Without one, every application instance defends itself.";
    public override string Mitigation => "Place an API gateway or WAF in front of public applications and make it the only ingress path.";
    public override string? FixHint => "add-component gateway; re-route user flows through it";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        foreach (var c in g.Model.Components)
        {
            if (c.Type is not (ComponentType.WebApp or ComponentType.Api)) continue;
            var direct = g.Inbound(c.Id).Where(f => g.From(f).Type == ComponentType.User).ToList();
            if (direct.Count == 0) continue;
            yield return new RuleMatch($"Users reach {c.Name} directly; no gateway, WAF or CDN in front.", Ids(c.Id), direct.Select(f => f.Id).ToList());
        }
    }
}

public sealed class NoIdentityProvider : Rule
{
    public override string Id => "S12";
    public override FindingCategory Category => FindingCategory.Security;
    public override string Tag => "Spoofing";
    public override Severity DefaultSeverity => Severity.Low;
    public override string Title => "Authentication mechanism not modeled";
    public override string Rationale => "If nobody can point at where identities are issued and verified, authentication is probably ad hoc per service, which is where bypasses come from.";
    public override string Mitigation => "Model the identity provider explicitly and route all user and service authentication through it.";

    public override IEnumerable<RuleMatch> Evaluate(ModelGraph g)
    {
        if (g.Model.Components.Any(c => c.Type == ComponentType.Identity)) yield break;
        if (!g.Model.Components.Any(c => c.Type == ComponentType.User)) yield break;
        yield return new RuleMatch("Users are present but no identity provider is modeled.", None, None, Confidence.Medium);
    }
}
