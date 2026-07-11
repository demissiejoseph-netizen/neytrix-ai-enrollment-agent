using NeytrixAI.Api.Middleware;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Auth;
using NeytrixAI.Infrastructure.Data;
using NeytrixAI.Infrastructure.Data.Repositories;
using NeytrixAI.Infrastructure.Resilience;
using NeytrixAI.Infrastructure.Services;
using Stripe;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

// Options
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.Configure<GoogleCalendarOptions>(builder.Configuration.GetSection("GoogleCalendar"));
// Canonical, host-agnostic env var names for the calendar secret + target calendar.
// Containers/serverless typically inject secrets as flat env vars, so these take
// precedence over the "GoogleCalendar__*" section binding when present.
builder.Services.PostConfigure<GoogleCalendarOptions>(opts =>
{
    var json = Environment.GetEnvironmentVariable("GOOGLE_CALENDAR_SERVICE_ACCOUNT_JSON");
    if (!string.IsNullOrWhiteSpace(json)) opts.ServiceAccountKeyJson = json;

    var calendarId = Environment.GetEnvironmentVariable("GOOGLE_CALENDAR_ID");
    if (!string.IsNullOrWhiteSpace(calendarId)) opts.CalendarId = calendarId;
});
builder.Services.Configure<ClerkOptions>(builder.Configuration.GetSection("Clerk"));

// Optional Clerk identity verification (JWT against Clerk's JWKS endpoint).
// Singletons: the JWKS ConfigurationManager caches/rotates keys across requests.
builder.Services.AddSingleton<IClerkSigningKeyProvider, ClerkJwksSigningKeyProvider>();
builder.Services.AddSingleton<IClerkTokenVerifier, ClerkTokenVerifier>();

// Data access
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IGuardianRepository, GuardianRepository>();
builder.Services.AddScoped<IPlayerRepository, PlayerRepository>();
builder.Services.AddScoped<IProgramRepository, ProgramRepository>();
builder.Services.AddScoped<IRegistrationRepository, RegistrationRepository>();

// Domain services
builder.Services.AddSingleton<EligibilityEngine>();

// Resilience: shared timeout/retry/circuit-breaker for all outbound third-party
// calls. Singleton so circuit state is shared across requests.
builder.Services.AddSingleton(new ResilienceOptions());
builder.Services.AddSingleton<ResilientExecutor>();

// External adapters. The Stripe SDK gets its own network-level timeout and retry
// budget in addition to the ResilientExecutor wrapper around the adapter call.
builder.Services.AddSingleton(_ =>
{
    var secretKey = builder.Configuration["Stripe:SecretKey"] ?? "sk_test_placeholder";
    var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var stripeHttpClient = new SystemNetHttpClient(httpClient, maxNetworkRetries: 2);
    return new StripeClient(secretKey, httpClient: stripeHttpClient);
});
builder.Services.AddScoped<IStripeAdapter, StripeAdapter>();
builder.Services.AddScoped<IGoogleCalendarAdapter, GoogleCalendarAdapter>();

// Orchestration
builder.Services.AddScoped<EnrollmentOrchestrationService>();
builder.Services.AddScoped<IAgentOrchestrationService, AgentOrchestrationService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.UseTenantResolution();
// Optional: resolve a Clerk identity when a valid bearer token is present. Runs
// after tenant resolution so guardian resolution has both tenant + identity, and
// never blocks a request — anonymous sessions proceed unchanged.
app.UseClerkAuthentication();
app.MapControllers();

app.Run();

public partial class Program { }
