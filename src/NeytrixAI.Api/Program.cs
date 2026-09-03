using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using NeytrixAI.Api.Services;
using NeytrixAI.Api.Middleware;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Data;
using NeytrixAI.Infrastructure.Data.Repositories;
using NeytrixAI.Infrastructure.Services;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((_, _, loggerConfiguration) => loggerConfiguration
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

var allowedOrigins = (builder.Configuration["ALLOWED_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (allowedOrigins.Contains("*", StringComparer.Ordinal))
{
    Log.Warning("ALLOWED_ORIGINS contains '*' but the widget CORS policy uses credentials; no origins will be allowed.");
    allowedOrigins = Array.Empty<string>();
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessHealthCheck>("postgres", tags: ["ready"]);
builder.Services.AddHttpContextAccessor();
builder.Services.AddCors(options => options.AddPolicy("widget", policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}));

builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.Configure<GoogleCalendarOptions>(builder.Configuration.GetSection("GoogleCalendar"));

// Singleton: DbConnectionFactory now owns one shared NpgsqlDataSource (built via
// NpgsqlDataSourceBuilder().UseVector()) rather than a bare NpgsqlConnection per call - pgvector's
// Npgsql plugin only registers its type mapping on a specific NpgsqlDataSource (Npgsql 8+ removed
// the old process-wide GlobalTypeMapper), so every connection must come from that one instance,
// and rebuilding a whole connection pool per DI scope would defeat pooling anyway.
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.ITenantRepository, TenantRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IGuardianRepository, GuardianRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IPlayerRepository, PlayerRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IProgramRepository, ProgramRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IRegistrationRepository, RegistrationRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IAssessmentRepository, AssessmentRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<NeytrixAI.Domain.Repositories.IKnowledgeChunkRepository, NeytrixAI.Infrastructure.Data.Repositories.KnowledgeChunkRepository>();
builder.Services.AddSingleton(_ => new Stripe.StripeClient(builder.Configuration["Stripe:SecretKey"] ?? string.Empty));
builder.Services.AddScoped<IStripeAdapter, StripeAdapter>();
builder.Services.AddScoped<IGoogleCalendarAdapter, GoogleCalendarAdapter>();
builder.Services.AddSingleton<NeytrixAI.Domain.Services.ConversationStateMachine>();
builder.Services.AddSingleton<NeytrixAI.Domain.Services.EligibilityEngine>();
builder.Services.AddScoped<IToolExecutionService, ToolExecutionService>();
builder.Services.AddScoped<IAgentOrchestrationService, AgentOrchestrationService>();

if (string.IsNullOrWhiteSpace(builder.Configuration["VertexAI:ProjectId"]))
    builder.Services.AddSingleton<IAgentModelClient, NullAgentModelClient>();
else
    builder.Services.AddSingleton<IAgentModelClient, VertexAgentModelClient>();

// GAP-04: real RAG embeddings for answer_faq. Fails closed to NullEmbeddingService (throws
// EmbeddingUnavailableException, handled by ToolExecutionService.AnswerFaqAsync as an escalation)
// when Vertex isn't configured, mirroring IAgentModelClient's fallback above.
if (string.IsNullOrWhiteSpace(builder.Configuration["VertexAI:ProjectId"]))
    builder.Services.AddSingleton<IEmbeddingService, NullEmbeddingService>();
else
    builder.Services.AddSingleton<IEmbeddingService, VertexEmbeddingService>();
builder.Services.AddScoped<IKnowledgeIngestionService, KnowledgeIngestionService>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("widget");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/chat/webhooks/stripe"))
        context.Request.EnableBuffering();
    await next();
});
app.UseTenantResolution();

app.MapGet("/healthz", () => Results.Ok());
// Google's edge frontend intercepts the well-known "/healthz" path on *.run.app
// public URLs and never forwards it to the container (confirmed empirically:
// every other path, including /readyz and random 404s, carries an
// x-cloud-trace-context header proving it reached this app; /healthz never
// does). Keep /healthz for internal-only checks that bypass the public edge
// (the Cloud Build smoke test curls 127.0.0.1 inside the build container, and
// Cloud Run's own --startup-probe is evaluated by the control plane directly),
// and use /health/live for anything that hits the public URL. It shares the
// "/health" prefix so TenantMiddleware still bypasses tenant resolution for it.
app.MapGet("/health/live", () => Results.Ok());
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapControllers();

await app.RunAsync();
