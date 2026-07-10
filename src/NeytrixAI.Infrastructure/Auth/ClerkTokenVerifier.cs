using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// Standard "verify a JWT against a JWKS endpoint" verifier for Clerk session
/// tokens. Signature, issuer and lifetime are validated with the public keys
/// supplied by <see cref="IClerkSigningKeyProvider"/>.
///
/// Fail-closed by construction: every failure path (disabled, blank token, no
/// keys, bad signature, wrong issuer, expired, malformed, JWKS fetch error)
/// returns <c>null</c> so the caller treats the request as anonymous. It must
/// NEVER throw for a caller-supplied token and must NEVER return an identity it
/// did not cryptographically verify.
/// </summary>
public sealed class ClerkTokenVerifier : IClerkTokenVerifier
{
    private readonly ClerkOptions _options;
    private readonly IClerkSigningKeyProvider _keyProvider;
    private readonly ILogger<ClerkTokenVerifier> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public ClerkTokenVerifier(
        IOptions<ClerkOptions> options,
        IClerkSigningKeyProvider keyProvider,
        ILogger<ClerkTokenVerifier> logger)
    {
        _options = options.Value;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    public async Task<ClerkIdentity?> VerifyAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.FrontendApiUrl))
            return null;

        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var keys = await _keyProvider.GetSigningKeysAsync(cancellationToken);
            if (keys.Count == 0)
            {
                _logger.LogWarning("Clerk verification skipped: no signing keys available (JWKS unavailable). Treating request as anonymous.");
                return null;
            }

            var issuer = _options.FrontendApiUrl!.TrimEnd('/');
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                ValidateIssuer = true,
                // Clerk issues tokens with iss = frontend API URL. Accept both the
                // trimmed form and the trailing-slash form to be robust.
                ValidIssuers = new[] { issuer, issuer + "/" },
                // Clerk session tokens do not carry an `aud` claim; authorized-party
                // (`azp`) is checked separately below when configured.
                ValidateAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(Math.Max(0, _options.ClockSkewSeconds))
            };

            var result = await _handler.ValidateTokenAsync(token, validationParameters);
            if (!result.IsValid)
            {
                _logger.LogInformation("Clerk token rejected: {Reason}. Treating request as anonymous.",
                    result.Exception?.GetType().Name ?? "invalid");
                return null;
            }

            var claims = result.ClaimsIdentity;

            if (!IsAuthorizedParty(claims))
            {
                _logger.LogWarning("Clerk token rejected: authorized party (azp) not permitted. Treating request as anonymous.");
                return null;
            }

            var userId = FirstNonEmpty(claims, "sub");
            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("Clerk token accepted signature but had no subject; treating as anonymous.");
                return null;
            }

            var email = FirstNonEmpty(claims, "email", "email_address", "primary_email_address");
            var firstName = FirstNonEmpty(claims, "first_name", "given_name");
            var lastName = FirstNonEmpty(claims, "last_name", "family_name");

            // If only a full `name` claim is present, split it best-effort.
            if (firstName is null && lastName is null)
            {
                var fullName = FirstNonEmpty(claims, "name");
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    var parts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    firstName = parts.Length > 0 ? parts[0] : null;
                    lastName = parts.Length > 1 ? parts[1] : null;
                }
            }

            return new ClerkIdentity(userId!, email, firstName, lastName);
        }
        catch (Exception ex)
        {
            // Fail closed: any unexpected error (network, parsing, etc.) means we
            // could not prove identity, so the request is anonymous — never an error
            // that could bypass a downstream check.
            _logger.LogWarning(ex, "Clerk token verification threw; treating request as anonymous.");
            return null;
        }
    }

    private bool IsAuthorizedParty(System.Security.Claims.ClaimsIdentity? claims)
    {
        if (_options.AuthorizedParties.Count == 0)
            return true;

        var azp = claims?.FindFirst("azp")?.Value;
        if (string.IsNullOrEmpty(azp))
            return true; // azp absent → nothing to enforce against

        return _options.AuthorizedParties.Contains(azp, StringComparer.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(System.Security.Claims.ClaimsIdentity? claims, params string[] types)
    {
        if (claims is null) return null;
        foreach (var type in types)
        {
            var value = claims.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }
}
