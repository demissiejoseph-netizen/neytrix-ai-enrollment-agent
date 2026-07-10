namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// Verifies a Clerk session token (JWT). The contract is deliberately
/// fail-closed: a valid token yields a <see cref="ClerkIdentity"/>; ANY other
/// outcome — missing/blank token, bad signature, wrong issuer, expired, malformed,
/// unreachable JWKS, or Clerk auth disabled — yields <c>null</c>, i.e. "treat as
/// anonymous". It never throws for a bad token and never returns a partially
/// trusted identity.
/// </summary>
public interface IClerkTokenVerifier
{
    Task<ClerkIdentity?> VerifyAsync(string? token, CancellationToken cancellationToken = default);
}
