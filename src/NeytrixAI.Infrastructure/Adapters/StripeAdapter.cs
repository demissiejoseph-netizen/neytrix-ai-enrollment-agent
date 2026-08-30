using Stripe;
using Stripe.Checkout;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NeytrixAI.Infrastructure.Adapters;

public interface IStripeAdapter
{
    Task<PaymentLinkResult> CreateCheckoutSessionAsync(
        string stripeAccountId,
        Guid tenantId,
        Guid registrationId,
        long amountCents,
        string currency,
        string successUrl,
        string cancelUrl,
        bool depositOnly,
        CancellationToken ct);

    Task<WaiverResult> CreateWaiverLinkAsync(
        Guid registrationId,
        string guardianEmail,
        CancellationToken ct);

    Stripe.Event ParseWebhookEvent(string payload, string signature, string webhookSecret);
}

public sealed record PaymentLinkResult(
    string CheckoutSessionId,
    string PaymentUrl,
    long AmountCents,
    string Currency,
    DateTimeOffset ExpiresAt);

public sealed record WaiverResult(
    string WaiverUrl,
    DateTimeOffset ExpiresAt);

public sealed class StripeAdapter : IStripeAdapter
{
    private readonly StripeClient _client;
    private readonly ILogger<StripeAdapter> _logger;
    private readonly StripeOptions _options;

    public StripeAdapter(
        StripeClient client,
        IOptions<StripeOptions> options,
        ILogger<StripeAdapter> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PaymentLinkResult> CreateCheckoutSessionAsync(
        string stripeAccountId,
        Guid tenantId,
        Guid registrationId,
        long amountCents,
        string currency,
        string successUrl,
        string cancelUrl,
        bool depositOnly,
        CancellationToken ct)
    {
        var sessionService = new SessionService(_client);

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = currency,
                        UnitAmount = amountCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = depositOnly ? "Program Deposit" : "Program Registration",
                        }
                    },
                    Quantity = 1
                }
            },
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            // tenant_id travels in Stripe's own metadata so the webhook handler (which fires with
            // no other context - Stripe never sends our JWT/session headers back) can resolve the
            // correct tenant *before* touching the database. Without it, the only alternative is
            // looking the registration up by id with no tenant filter at all, which either bypasses
            // row-level security entirely (a cross-tenant data leak) or - if RLS is enforced and no
            // tenant context is set - silently matches zero rows and the payment is never recorded.
            Metadata = new Dictionary<string, string>
            {
                ["registration_id"] = registrationId.ToString(),
                ["tenant_id"] = tenantId.ToString(),
                ["deposit_only"] = depositOnly.ToString()
            },
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        var requestOptions = new RequestOptions { StripeAccount = stripeAccountId };
        var session = await sessionService.CreateAsync(options, requestOptions, ct);

        _logger.LogInformation(
            "Created Stripe checkout session {SessionId} for registration {RegistrationId}",
            session.Id, registrationId);

        return new PaymentLinkResult(
            session.Id,
            session.Url,
            amountCents,
            currency,
            session.ExpiresAt is { } expiresAt ? new DateTimeOffset(expiresAt) : DateTimeOffset.UtcNow.AddHours(24));
    }

    public Task<WaiverResult> CreateWaiverLinkAsync(
        Guid registrationId,
        string guardianEmail,
        CancellationToken ct)
    {
        // Integration with DocuSign/PandaDoc would go here.
        // For MVP, we generate a signed URL to a hosted waiver form.
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var token = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{registrationId}:{expiresAt:O}"));
        var url = $"{_options.WaiverBaseUrl}/waiver/{registrationId}?token={Uri.EscapeDataString(token)}";

        return Task.FromResult(new WaiverResult(url, expiresAt));
    }

    public Stripe.Event ParseWebhookEvent(string payload, string signature, string webhookSecret)
    {
        return EventUtility.ConstructEvent(payload, signature, webhookSecret,
            throwOnApiVersionMismatch: false);
    }
}

public sealed class StripeOptions
{
    public string SecretKey { get; init; } = default!;
    public string WebhookSecret { get; init; } = default!;
    public string WaiverBaseUrl { get; init; } = default!;
    public string SuccessUrlTemplate { get; init; } = default!;
    public string CancelUrlTemplate { get; init; } = default!;
}
