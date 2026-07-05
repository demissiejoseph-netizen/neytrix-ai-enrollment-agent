using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

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
    private readonly CalendarService _calendarService;
    private readonly ILogger<GoogleCalendarAdapter> _logger;
    private readonly GoogleCalendarOptions _options;

    public GoogleCalendarAdapter(
        IOptions<GoogleCalendarOptions> options,
        ILogger<GoogleCalendarAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;

        var credential = GoogleCredential
            .FromJson(_options.ServiceAccountKeyJson)
            .CreateScoped(CalendarService.Scope.Calendar);

        _calendarService = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Neytrix AI Enrollment Agent"
        });
    }

    public async Task<IReadOnlyList<AvailableSlot>> GetAvailableSlotsAsync(
        string calendarId,
        DateOnly weekOf,
        int durationMinutes,
        CancellationToken ct)
    {
        var start = weekOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(7);

        // Get free/busy to identify available windows
        var freeBusyRequest = new FreeBusyRequest
        {
            TimeMinDateTimeOffset = start,
            TimeMaxDateTimeOffset = end,
            Items = new List<FreeBusyRequestItem> { new() { Id = calendarId } }
        };

        var freeBusy = await _calendarService.Freebusy
            .Query(freeBusyRequest)
            .ExecuteAsync(ct);

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

        var created = await _calendarService.Events
            .Insert(calEvent, calendarId)
            .ExecuteAsync(ct);

        _logger.LogInformation(
            "Booked calendar event {EventId} for player {PlayerName}",
            created.Id, playerName);

        return new BookedEvent(created.Id, startTime, created.HtmlLink);
    }

    public async Task CancelEventAsync(string calendarId, string eventId, CancellationToken ct)
    {
        await _calendarService.Events.Delete(calendarId, eventId).ExecuteAsync(ct);
        _logger.LogInformation("Cancelled calendar event {EventId}", eventId);
    }
}

public sealed class GoogleCalendarOptions
{
    public string ServiceAccountKeyJson { get; init; } = default!;
    public string DefaultLocation { get; init; } = string.Empty;
    public int DefaultAssessmentDurationMinutes { get; init; } = 60;
}
