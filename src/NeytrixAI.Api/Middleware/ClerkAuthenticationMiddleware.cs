using NeytrixAI.Infrastructure.Auth;

namespace NeytrixAI.Api.Middleware;

/// <summary>
/// OPTIONAL Clerk identity resolution. If the request carries a valid Clerk
/// session token (Authorization: Bearer &lt;jwt&gt;), the verified
/// <see cref="ClerkIdentity"/> is stashed in <c>HttpContext.Items</c> for
/// downstream use. Otherwise the request proceeds completely unchanged.
///
/// This middleware NEVER rejects a request: authentication is a capability, not a
/// requirement. A missing, malformed, expired or otherwise invalid token simply
/// leaves the request anonymous — exactly like the pre-existing widget flow. It
/// therefore does not (and must not) gate any endpoint or alter any existing
/// consent/payment/eligibility/tenant behaviour.
/// </summary>
public sealed class ClerkAuthenticationMiddleware
{
    public const string ClerkIdentityItemKey = "ClerkIdentity";

    private readonly RequestDelegate _next;
    private readonly ILogger<ClerkAuthenticationMiddleware> _logger;

    public ClerkAuthenticationMiddleware(RequestDelegate next, ILogger<ClerkAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IClerkTokenVerifier verifier)
    {
        var token = ExtractBearerToken(context);
        if (!string.IsNullOrWhiteSpace(token))
        {
            // VerifyAsync is fail-closed and never throws for a bad token, but we
            // still guard here so a verifier bug can never take down the request.
            try
            {
                var identity = await verifier.VerifyAsync(token, context.RequestAborted);
                if (identity is not null)
                {
                    context.Items[ClerkIdentityItemKey] = identity;
                    _logger.LogInformation("Resolved Clerk identity {UserId} for request.", identity.UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clerk authentication middleware swallowed an error; continuing anonymously.");
            }
        }

        await _next(context);
    }

    private static string? ExtractBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}

public static class ClerkAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseClerkAuthentication(this IApplicationBuilder app)
        => app.UseMiddleware<ClerkAuthenticationMiddleware>();
}
