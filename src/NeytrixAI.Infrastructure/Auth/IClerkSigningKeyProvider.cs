using Microsoft.IdentityModel.Tokens;

namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// Supplies the current set of public signing keys used to verify Clerk session
/// tokens. Abstracted so the verification logic can be unit-tested against an
/// in-memory key without any network access, while production fetches (and
/// caches/rotates) the keys from Clerk's JWKS endpoint.
/// </summary>
public interface IClerkSigningKeyProvider
{
    Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken = default);
}
