using NeytrixAI.Api.Middleware;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Data;
using NeytrixAI.Infrastructure.Data.Repositories;
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

// Data access
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IGuardianRepository, GuardianRepository>();
builder.Services.AddScoped<IPlayerRepository, PlayerRepository>();
builder.Services.AddScoped<IProgramRepository, ProgramRepository>();
builder.Services.AddScoped<IRegistrationRepository, RegistrationRepository>();

// Domain services
builder.Services.AddSingleton<EligibilityEngine>();

// External adapters
builder.Services.AddSingleton(_ => new StripeClient(builder.Configuration["Stripe:SecretKey"] ?? "sk_test_placeholder"));
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
app.MapControllers();

app.Run();

public partial class Program { }
