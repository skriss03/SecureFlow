using System.Security.Cryptography;
using System.Text;
using SecureFlow.Api.Options;

namespace SecureFlow.Api.Auth;

/// <summary>
/// Shared-token auth for /api, off by default so the local demo needs nothing. When enabled, write
/// requests (scans, fixes, deletes) need a configured token; reads stay open unless ProtectReads is set,
/// so report links keep working for anyone on the network. The SPA and static files are never gated.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecureFlowOptions.AuthOptions _options;
    private readonly byte[][] _tokens;

    public TokenAuthMiddleware(RequestDelegate next, SecureFlowOptions options)
    {
        _next = next;
        _options = options.Auth;
        _tokens = _options.Tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Encoding.UTF8.GetBytes(t.Trim())).ToArray();
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!_options.Enabled || !ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health"))
        {
            await _next(ctx);
            return;
        }
        var isRead = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method);
        if (isRead && !_options.ProtectReads)
        {
            await _next(ctx);
            return;
        }

        if (!IsAuthorized(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = _options.Scheme;
            await ctx.Response.WriteAsJsonAsync(new { error = $"A SecureFlow token is required. Send '{_options.HeaderName}: {_options.Scheme} <token>' (CLI: --token or SECUREFLOW_TOKEN)." });
            return;
        }
        await _next(ctx);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (_tokens.Length == 0) return false;
        var header = request.Headers[_options.HeaderName].ToString();
        var prefix = _options.Scheme + " ";
        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : header.Trim();
        if (token.Length == 0) return false;
        var presented = Encoding.UTF8.GetBytes(token);
        return _tokens.Any(t => CryptographicOperations.FixedTimeEquals(t, presented));
    }
}
