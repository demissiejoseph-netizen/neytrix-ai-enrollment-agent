using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using NeytrixAI.Infrastructure.Resilience;

namespace NeytrixAI.Infrastructure.Adapters;

public interface IGoogleCalendarAdapter
{
    Task<IReadOnlyList<AvailableSlot>> GetAvailableSlotsAsync(
        string calendarId,
        DateOnly weekOf,
        int durationMinutes,
        CancellationToken ct);

    Task<BookedEvent> BookSlotAsync(
        string calendarId,
        string slotId,
        string guardianName,
        string guardianEmail,
        string playerName,
        string programName,
        CancellationToken ct);

    Task CancelEventAsync(
        string calendarId,
        string eventId,
        CancellationToken ct);
}

public sealed record AvailableSlot(
    string SlotId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int DurationMinutes,
    string? Location);

public sealed record BookedEvent(
    string EventId,
    DateTimeOffset StartsAt,
    string HtmlLink);

public sealed class GoogleCalendarAdapter : IGoogleCalendarAdapter
{
    private readonly Lazy<CalendarService> _calendarServiceFactory;
    private readonly ILogger<GoogleCalendarAdapter> _logger;
    private readonly GoogleCalendarOptions _options;
    private readonly ResilientExecutor _resilience;

    private CalendarService _calendarService => _calendarServiceFactory.Value;

    public GoogleCalendarAdapter(
        IOptions<GoogleCalendarOptions> options,
        ResilientExecutor resilience,
        ILogger<GoogleCalendarAdapter> logger)
    {
        _options = options.Value;
        _resilience = resilience;
        _logger = logger;

        // Built lazily so the service can be constructed (and the rest of the app
        // can run) even when Google Calendar is not yet configured. A missing key
        // fails only the calendar operation itself, not the whole enrolment flow.
        _calendarServiceFactory = new Lazy<CalendarService>(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.ServiceAccountKeyJson))
                throw new InvalidOperationException("Google Calendar is not configured (missing ServiceAccountKeyJson).");

            var credential = GoogleCredential
                .FromJson(_options.ServiceAccountKeyJson)
                .CreateScoped(CalendarService.Scope.Calendar);

            return new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Neytrix AI Enrollment Agent"
            });
        });
    }

    // Prefer a caller-supplied calendar id (e.g. the tenant's own calendar); fall
    // back to the deployment default from configuration. Both empty is a
    // misconfiguration and fails closed with a clear message.
    private string ResolveCalendarId(string calendarId) =>
        !string.IsNullOrWhiteSpace(calendarId) ? calendarId
        : !string.IsNullOrWhiteSpace(_options.CalendarId) ? _options.CalendarId
        : throw new InvalidOperationException(
            "Google Calendar is not configured (no calendar id supplied and GOOGLE_CALENDAR_ID is unset).");

    public async Task<IReadOnlyList<AvailableSlot>> GetAvailableSlotsAsync(
        string calendarId,
        DateOnly weekOf,
        int durationMinutes,
        CancellationToken ct)
    {
        calendarId = ResolveCalendarId(calendarId);
        var start = weekOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(7);

        // Get free/busy to identify available windows
        var freeBusyRequest = new FreeBusyRequest
        {
            TimeMinDateTimeOffset = start,
            TimeMaxDateTimeOffset = end,
            Items = new List<FreeBusyRequestItem> { new() { Id = calendarId } }
        };

        var freeBusy = await _resilience.ExecuteAsync(
            "gcal.freebusy.query",
            token => _calendarService.Freebusy.Query(freeBusyRequest).ExecuteAsync(token),
            ct);

        var busyPeriods = freeBusy.Calendars.TryGetValue(calendarId, out var cal)
            ? cal.Busy ?? new List<TimePeriod>()
            : new List<TimePeriod>();

        // Generate slots in working hours (9am-5pm) that don't overlap busy periods
        var slots = new List<AvailableSlot>();
        var current = start.AddHours(9); // Start at 9am

        while (current < end)
        {
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
            {
                var slotEnd = current.AddMinutes(durationMinutes);
                var isBusy = busyPeriods.Any(b =>
                    b.StartDateTimeOffset < slotEnd && b.EndDateTimeOffset > current);

                if (!isBusy && current.Hour < 17)
                {
                    var slotId = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(current.ToString("O")));

                    slots.Add(new AvailableSlot(
                        slotId,
                        current,
                        slotEnd,
                        durationMinutes,
                        _options.DefaultLocation));
                }
            }

            current = current.AddMinutes(durationMinutes + 15); // 15-min buffer
        }

        return slots.Take(10).ToList().AsReadOnly();
    }

    public async Task<BookedEvent> BookSlotAsync(
        string calendarId,
        string slotId,
        string guardianName,
        string guardianEmail,
        string playerName,
        string programName,
        CancellationToken ct)
    {
        calendarId = ResolveCalendarId(calendarId);
        var startTime = DateTimeOffset.Parse(
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(slotId)));

        var calEvent = new Event
        {
            Summary = $"Assessment: {playerName} - {programName}",
            Description = $"Guardian: {guardianName} ({guardianEmail})\nPlayer: {playerName}\nProgram: {programName}",
            Location = _options.DefaultLocation,
            Start = new EventDateTime { DateTimeDateTimeOffset = startTime },
            End = new EventDateTime { DateTimeDateTimeOffset = startTime.AddHours(1) },
            Attendees = new List<EventAttendee>
            {
                new() { Email = guardianEmail, DisplayName = guardianName }
            },
            Reminders = new Event.RemindersData
            {
                UseDefault = false,
                Overrides = new List<EventReminder>
                {
                    new() { Method = "email", Minutes = 1440 }, // 24h
                    new() { Method = "email", Minutes = 60 }    // 1h
                }
            }
        };

        var created = await _resilience.ExecuteAsync(
            "gcal.events.insert",
            token => _calendarService.Events.Insert(calEvent, calendarId).ExecuteAsync(token),
            ct);

        _logger.LogInformation(
            "Booked calendar event {EventId} for player {PlayerName}",
            created.Id, playerName);

        return new BookedEvent(created.Id, startTime, created.HtmlLink);
    }

    public async Task CancelEventAsync(string calendarId, string eventId, CancellationToken ct)
    {
        calendarId = ResolveCalendarId(calendarId);
        await _resilience.ExecuteAsync(
            "gcal.events.delete",
            async token =>
            {
                await _calendarService.Events.Delete(calendarId, eventId).ExecuteAsync(token);
                return true;
            },
            ct);
        _logger.LogInformation("Cancelled calendar event {EventId}", eventId);
    }
}

public sealed class GoogleCalendarOptions
{
    /// <summary>
    /// Full service-account key JSON (single escaped string). Injected from the
    /// environment (see <c>GOOGLE_CALENDAR_SERVICE_ACCOUNT_JSON</c>) rather than a
    /// file path, so container/serverless hosts can supply it as a secret env var.
    /// Settable so environment overrides can be applied after section binding.
    /// </summary>
    public string ServiceAccountKeyJson { get; set; } = string.Empty;

    /// <summary>
    /// Deployment-default calendar to book into (<c>GOOGLE_CALENDAR_ID</c>). Used
    /// only when a caller does not supply a calendar id of its own; multi-tenant
    /// callers pass the tenant's own calendar id, which always takes precedence.
    /// </summary>
    public string CalendarId { get; set; } = string.Empty;

    public string DefaultLocation { get; set; } = string.Empty;
    public int DefaultAssessmentDurationMinutes { get; set; } = 60;
}
