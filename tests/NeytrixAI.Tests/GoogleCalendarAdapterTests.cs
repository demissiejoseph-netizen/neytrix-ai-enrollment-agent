using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Resilience;
using Xunit;

namespace NeytrixAI.Tests;

// Fail-closed contract for the Google Calendar adapter. Missing/invalid calendar
// credentials must NOT crash a booking attempt or hang the conversation — the
// attempt surfaces as ExternalServiceUnavailableException (the shared escalation
// signal), exactly like the Stripe path. The adapter is lazy-initialised so the
// rest of the app boots even when calendar config is absent.
public sealed class GoogleCalendarAdapterTests
{
    private static GoogleCalendarAdapter Build(GoogleCalendarOptions options)
    {
        // Fast resilience so the test does not wait on real backoff delays.
        var resilience = new ResilientExecutor(
            new ResilienceOptions { MaxRetries = 0, BaseDelay = TimeSpan.FromMilliseconds(1) },
            NullLogger<ResilientExecutor>.Instance);

        return new GoogleCalendarAdapter(
            Options.Create(options), resilience, NullLogger<GoogleCalendarAdapter>.Instance);
    }

    private static string ValidSlotId() =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.AddDays(1).ToString("O")));

    [Fact]
    public async Task BookSlot_MissingServiceAccountJson_FailsClosed_DoesNotCrash()
    {
        // Calendar id is present, but the credentials env var was never set.
        var adapter = Build(new GoogleCalendarOptions { ServiceAccountKeyJson = "" });

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            adapter.BookSlotAsync(
                "cal_123", ValidSlotId(), "Jane Doe", "jane@example.com",
                "Sam Doe", "Youth Soccer", CancellationToken.None));
    }

    [Fact]
    public async Task BookSlot_NoCalendarIdAndNoDefault_FailsClosed()
    {
        var adapter = Build(new GoogleCalendarOptions { ServiceAccountKeyJson = "", CalendarId = "" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.BookSlotAsync(
                "", ValidSlotId(), "Jane Doe", "jane@example.com",
                "Sam Doe", "Youth Soccer", CancellationToken.None));
    }

    [Fact]
    public async Task BookSlot_InvalidCredentialsJson_FailsClosed_DoesNotCrash()
    {
        var adapter = Build(new GoogleCalendarOptions
        {
            ServiceAccountKeyJson = "{ not-a-valid-service-account-key }",
            CalendarId = "cal_default"
        });

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            adapter.BookSlotAsync(
                "", ValidSlotId(), "Jane Doe", "jane@example.com",
                "Sam Doe", "Youth Soccer", CancellationToken.None));
    }
}
