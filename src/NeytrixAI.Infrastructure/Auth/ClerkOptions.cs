namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// Configuration for optional Clerk (clerk.com) identity verification.
///
/// Bound from the "Clerk" configuration section. Everything is optional: if
/// <see cref="Enabled"/> is false or <see cref="FrontendApiUrl"/> is blank, the
/// verifier short-circuits and every request is treated as anonymous — the
/// existing unauthenticated widget flow is completely unaffected.
/// </summary>
public sealed class ClerkOptions
{
    /// <summary>
    /// Master switch. Auth verification only runs when this is true AND a
    /// FrontendApiUrl is configured. Defaults to true so that supplying the URL
    /// is enough to turn the capability on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Clerk Frontend API origin, e.g. https://fun-goldfish-86.clerk.accounts.dev.
    /// This doubles as the expected token issuer (Clerk session JWTs carry this as
    /// their <c>iss</c> claim) and the base for the JWKS endpoint
    /// (<c>{FrontendApiUrl}/.well-known/jwks.json</c>).
    /// </summary>
    public string? FrontendApiUrl { get; set; }

    /// <summary>
    /// Allowed authorized-party (<c>azp</c>) origins. When non-empty, a token whose
    /// <c>azp</c> claim is present but not in this list is rejected. Left empty by
    /// default (azp is not enforced) to keep local/dev setups frictionless.
    /// </summary>
    public IList<string> AuthorizedParties { get; set; } = new List<string>();

    /// <summary>Permitted clock skew (seconds) when validating exp/nbf.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
