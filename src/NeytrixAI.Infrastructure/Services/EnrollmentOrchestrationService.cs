using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Adapters;
using System.Text.Json;

namespace NeytrixAI.Infrastructure.Services;

public class EnrollmentOrchestrationService
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IGuardianRepository _guardianRepository;
    private readonly IPlayerRepository _playerRepository;
    private readonly IProgramRepository _programRepository;
    private readonly IRegistrationRepository _registrationRepository;
    private readonly StripeAdapter _stripeAdapter;
    private readonly GoogleCalendarAdapter _calendarAdapter;

    public EnrollmentOrchestrationService(
        ITenantRepository tenantRepository,
        IGuardianRepository guardianRepository,
        IPlayerRepository playerRepository,
        IProgramRepository programRepository,
        IRegistrationRepository registrationRepository,
        StripeAdapter stripeAdapter,
        GoogleCalendarAdapter calendarAdapter)
    {
        _tenantRepository = tenantRepository;
        _guardianRepository = guardianRepository;
        _playerRepository = playerRepository;
        _programRepository = programRepository;
        _registrationRepository = registrationRepository;
        _stripeAdapter = stripeAdapter;
        _calendarAdapter = calendarAdapter;
    }

    public async Task<string> HandleToolCallAsync(string toolName, Dictionary<string, object> parameters, Guid tenantId)
    {
        return toolName switch
        {
            "search_programs" => await SearchProgramsAsync(parameters, tenantId),
            "check_eligibility" => await CheckEligibilityAsync(parameters, tenantId),
            "create_registration" => await CreateRegistrationAsync(parameters, tenantId),
            "process_payment" => await ProcessPaymentAsync(parameters, tenantId),
            "book_calendar_event" => await BookCalendarEventAsync(parameters, tenantId),
            _ => JsonSerializer.Serialize(new { error = "Unknown tool", tool = toolName })
        };
    }

    private async Task<string> SearchProgramsAsync(Dictionary<string, object> parameters, Guid tenantId)
    {
        var programs = await _programRepository.GetActiveAsync(tenantId);
        
        // Apply filters if provided
        if (parameters.ContainsKey("sport_type"))
        {
            var sportType = parameters["sport_type"].ToString();
            programs = programs.Where(p => p.SportType.Equals(sportType, StringComparison.OrdinalIgnoreCase));
        }

        if (parameters.ContainsKey("min_age") && int.TryParse(parameters["min_age"].ToString(), out var minAge))
        {
            programs = programs.Where(p => p.MinAge <= minAge && p.MaxAge >= minAge);
        }

        var result = programs.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            sport_type = p.SportType,
            age_range = $"{p.MinAge}-{p.MaxAge}",
            price = p.Price,
            schedule = p.Schedule,
            location = p.Location
        });

        return JsonSerializer.Serialize(new { programs = result });
    }

    private async Task<string> CheckEligibilityAsync(Dictionary<string, object> parameters, Guid tenantId)
    {
        if (!parameters.ContainsKey("program_id") || !parameters.ContainsKey("player_age"))
        {
            return JsonSerializer.Serialize(new { eligible = false, reason = "Missing required parameters" });
        }

        var programId = Guid.Parse(parameters["program_id"].ToString()!);
        var playerAge = int.Parse(parameters["player_age"].ToString()!);

        var program = await _programRepository.GetByIdAsync(programId, tenantId);
        if (program == null)
        {
            return JsonSerializer.Serialize(new { eligible = false, reason = "Program not found" });
        }

        var eligible = playerAge >= program.MinAge && playerAge <= program.MaxAge;
        var reason = eligible ? "Player meets age requirements" : $"Player must be between {program.MinAge}-{program.MaxAge} years old";

        return JsonSerializer.Serialize(new { eligible, reason, program_name = program.Name });
    }

    private async Task<string> CreateRegistrationAsync(Dictionary<string, object> parameters, Guid tenantId)
    {
        if (!parameters.ContainsKey("program_id") || !parameters.ContainsKey("player_id"))
        {
            return JsonSerializer.Serialize(new { success = false, error = "Missing required parameters" });
        }

        var registration = new Registration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProgramId = Guid.Parse(parameters["program_id"].ToString()!),
            PlayerId = Guid.Parse(parameters["player_id"].ToString()!),
            Status = "pending",
            PaymentStatus = "pending",
            RegistrationDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _registrationRepository.CreateAsync(registration);
        return JsonSerializer.Serialize(new { success = true, registration_id = created.Id });
    }

    private async Task<string> ProcessPaymentAsync(Dictionary<string, object> parameters, Guid tenantId)
    {
        if (!parameters.ContainsKey("registration_id") || !parameters.ContainsKey("amount"))
        {
            return JsonSerializer.Serialize(new { success = false, error = "Missing required parameters" });
        }

        var registrationId = Guid.Parse(parameters["registration_id"].ToString()!);
        var amount = decimal.Parse(parameters["amount"].ToString()!);

        var registration = await _registrationRepository.GetByIdAsync(registrationId, tenantId);
        if (registration == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "Registration not found" });
        }

        // Create Stripe payment intent
        var paymentIntentId = await _stripeAdapter.CreatePaymentIntentAsync(amount, "usd", new Dictionary<string, string>
        {
            { "registration_id", registrationId.ToString() },
            { "tenant_id", tenantId.ToString() }
        });

        // Update registration with payment intent
        registration.StripePaymentIntentId = paymentIntentId;
        registration.UpdatedAt = DateTime.UtcNow;
        await _registrationRepository.UpdateAsync(registration);

        return JsonSerializer.Serialize(new { success = true, payment_intent_id = paymentIntentId });
    }

    private async Task<string> BookCalendarEventAsync(Dictionary<string, object> parameters, Guid tenantId)
    {
        if (!parameters.ContainsKey("registration_id") || !parameters.ContainsKey("start_time") || !parameters.ContainsKey("end_time"))
        {
            return JsonSerializer.Serialize(new { success = false, error = "Missing required parameters" });
        }

        var registrationId = Guid.Parse(parameters["registration_id"].ToString()!);
        var startTime = DateTime.Parse(parameters["start_time"].ToString()!);
        var endTime = DateTime.Parse(parameters["end_time"].ToString()!);
        var summary = parameters.ContainsKey("summary") ? parameters["summary"].ToString()! : "Sports Program Session";

        var registration = await _registrationRepository.GetByIdAsync(registrationId, tenantId);
        if (registration == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "Registration not found" });
        }

        // Create calendar event
        var eventId = await _calendarAdapter.CreateEventAsync(summary, startTime, endTime);

        // Update registration with calendar event
        registration.CalendarEventId = eventId;
        registration.UpdatedAt = DateTime.UtcNow;
        await _registrationRepository.UpdateAsync(registration);

        return JsonSerializer.Serialize(new { success = true, event_id = eventId });
    }
}
