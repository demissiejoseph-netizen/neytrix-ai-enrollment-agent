using NeytrixAI.Infrastructure.Adapters;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// Deterministic Stripe test double. Never calls the network; returns canned values so the
/// end-to-end test can exercise send_waiver / create_payment_link without real Stripe secrets.
/// </summary>
public sealed class FakeStripeAdapter : IStripeAdapter
{
    public Task<PaymentLinkResult> CreateCheckoutSessionAsync(
        string stripeAccountId, Guid tenantId, Guid registrationId, long amountCents, string currency,
        string successUrl, string cancelUrl, bool depositOnly, CancellationToken ct) =>
        Task.FromResult(new PaymentLinkResult(
            CheckoutSessionId: $"cs_test_{registrationId:N}",
            PaymentUrl: $"https://checkout.stripe.test/pay/{registrationId:N}",
            AmountCents: amountCents,
            Currency: currency,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(24)));

    public Task<WaiverResult> CreateWaiverLinkAsync(Guid registrationId, string guardianEmail, CancellationToken ct) =>
        Task.FromResult(new WaiverResult(
            WaiverUrl: $"https://waivers.test/sign/{registrationId:N}",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)));

    public Stripe.Event ParseWebhookEvent(string payload, string signature, string webhookSecret) =>
        throw new NotSupportedException(
            "FakeStripeAdapter does not simulate webhook parsing - this end-to-end test does not exercise the payment-webhook completion path.");
}

/// <summary>
/// Deterministic Google Calendar test double. Never calls the network; returns canned slots
/// and a canned booked event so get_available_slots / book_assessment can be exercised.
/// </summary>
public sealed class FakeGoogleCalendarAdapter : IGoogleCalendarAdapter
{
    public Task<IReadOnlyList<AvailableSlot>> GetAvailableSlotsAsync(
        string calendarId, DateOnly weekOf, int durationMinutes, CancellationToken ct)
    {
        var mondayUtc = weekOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        IReadOnlyList<AvailableSlot> slots = new List<AvailableSlot>
        {
            new(
                $"slot-{weekOf:yyyyMMdd}-1",
                new DateTimeOffset(mondayUtc.AddHours(16), TimeSpan.Zero),
                new DateTimeOffset(mondayUtc.AddHours(16).AddMinutes(durationMinutes), TimeSpan.Zero),
                durationMinutes,
                "Field 1"),
            new(
                $"slot-{weekOf:yyyyMMdd}-2",
                new DateTimeOffset(mondayUtc.AddDays(2).AddHours(17), TimeSpan.Zero),
                new DateTimeOffset(mondayUtc.AddDays(2).AddHours(17).AddMinutes(durationMinutes), TimeSpan.Zero),
                durationMinutes,
                "Field 1")
        };
        return Task.FromResult(slots);
    }

    public Task<BookedEvent> BookSlotAsync(
        string calendarId, string slotId, string guardianName, string guardianEmail,
        string playerName, string programName, CancellationToken ct) =>
        Task.FromResult(new BookedEvent(
            EventId: $"evt_{slotId}",
            StartsAt: DateTimeOffset.UtcNow.AddDays(3),
            HtmlLink: $"https://calendar.test/event/{slotId}"));

    public Task CancelEventAsync(string calendarId, string eventId, CancellationToken ct) => Task.CompletedTask;
}
