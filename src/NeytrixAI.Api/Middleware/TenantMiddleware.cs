using NeytrixAI.Infrastructure.Repositories;

namespace NeytrixAI.Api.Middleware;

/// <summary>
/// Resolves the tenant from the X-Tenant-Slug header, sets app.tenant_id
/// in the PostgreSQL session so RLS policies apply automatically.
/// This is the SOLE cross-tenant isolation enforcement point.
/// </summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantRepository tenantRepo)
    {
        // Skip for health checks and webhooks (webhook auth handled separately)
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var slug = context.Request.Headers["X-Tenant-Slug"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(slug))
        {
            // Stripe webhooks use a different auth path
            if (path.Contains("webhooks/stripe", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "X-Tenant-Slug header is required." });
            return;
        }

        var tenant = await tenantRepo.GetBySlugAsync(slug, context.RequestAborted);

        if (tenant is null)
        {
            _logger.LogWarning("Unknown tenant slug: {Slug}", slug);
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = $"Tenant '{slug}' not found." });
            return;
        }

        if (!tenant.IsActive)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "This tenant account is inactive." });
            return;
        }

        // Store tenant context for downstream use
        context.Items["TenantId"] = tenant.Id;
        context.Items["Tenant"] = tenant;

        // Set PostgreSQL session variable for RLS
        await tenantRepo.SetTenantSessionAsync(tenant.Id, context.RequestAborted);

        await _next(context);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();
}
