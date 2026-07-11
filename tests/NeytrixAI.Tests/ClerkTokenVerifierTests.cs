using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NeytrixAI.Infrastructure.Auth;
using Xunit;

namespace NeytrixAI.Tests;

// Unit tests for Clerk session-token (JWT) verification. They exercise the exact
// contract the middleware relies on: a validly signed token yields an identity,
// and EVERY failure mode (expired, wrong issuer, wrong key, malformed, empty,
// disabled, JWKS unavailable) fails CLOSED to null ("anonymous") without throwing.
//
// No network access: an in-memory RSA key stands in for Clerk's JWKS so the
// signature/issuer/lifetime logic is tested deterministically.
public sealed class ClerkTokenVerifierTests
{
    private const string Issuer = "https://fun-goldfish-86.clerk.accounts.dev";

    // Signing key provider backed by an in-memory RSA key (public part only, as a
    // real JWKS would expose). Optionally returns no keys to simulate an
    // unavailable JWKS.
    private sealed class TestKeyProvider : IClerkSigningKeyProvider
    {
        private readonly SecurityKey[] _keys;
        public TestKeyProvider(params SecurityKey[] keys) => _keys = keys;
        public Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<SecurityKey>>(_keys);
    }

    private static RsaSecurityKey NewKey(string kid)
        => new(RSA.Create(2048)) { KeyId = kid };

    private static ClerkTokenVerifier BuildVerifier(
        IClerkSigningKeyProvider keyProvider,
        bool enabled = true,
        string? frontendApiUrl = Issuer)
    {
        var options = Options.Create(new ClerkOptions
        {
            Enabled = enabled,
            FrontendApiUrl = frontendApiUrl,
            ClockSkewSeconds = 5
        });
        return new ClerkTokenVerifier(options, keyProvider, NullLogger<ClerkTokenVerifier>.Instance);
    }

    private static string CreateToken(
        SecurityKey signingKey,
        string issuer = Issuer,
        string subject = "user_2abc123",
        DateTime? expires = null,
        IDictionary<string, object>? extraClaims = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (extraClaims is not null)
            foreach (var kv in extraClaims) claims[kv.Key] = kv.Value;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task ValidToken_ReturnsIdentity_WithClaims()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));
        var token = CreateToken(key, subject: "user_xyz", extraClaims: new Dictionary<string, object>
        {
            ["email"] = "parent@example.com",
            ["first_name"] = "Jane",
            ["last_name"] = "Doe"
        });

        var identity = await verifier.VerifyAsync(token);

        Assert.NotNull(identity);
        Assert.Equal("user_xyz", identity!.UserId);
        Assert.Equal("parent@example.com", identity.Email);
        Assert.Equal("Jane", identity.FirstName);
        Assert.Equal("Doe", identity.LastName);
    }

    [Fact]
    public async Task ValidToken_WithTrailingSlashIssuer_IsAccepted()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));
        var token = CreateToken(key, issuer: Issuer + "/");

        var identity = await verifier.VerifyAsync(token);

        Assert.NotNull(identity);
    }

    [Fact]
    public async Task ValidToken_WithOnlyFullNameClaim_SplitsName()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));
        var token = CreateToken(key, extraClaims: new Dictionary<string, object> { ["name"] = "Alex Smith" });

        var identity = await verifier.VerifyAsync(token);

        Assert.NotNull(identity);
        Assert.Equal("Alex", identity!.FirstName);
        Assert.Equal("Smith", identity.LastName);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsNull()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));
        // Expired well beyond the 5s configured clock skew.
        var token = CreateToken(key, expires: DateTime.UtcNow.AddMinutes(-10));

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task WrongIssuer_ReturnsNull()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));
        var token = CreateToken(key, issuer: "https://evil.example.com");

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task SignedWithUnknownKey_ReturnsNull()
    {
        // Provider exposes a DIFFERENT key than the one that signed the token.
        var signingKey = NewKey("attacker");
        var trustedKey = NewKey("trusted");
        var verifier = BuildVerifier(new TestKeyProvider(trustedKey));
        var token = CreateToken(signingKey);

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    [InlineData("aaa.bbb.ccc")]
    [InlineData("Bearer something")]
    public async Task MissingOrMalformedToken_ReturnsNull_WithoutThrowing(string? token)
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key));

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task Disabled_ReturnsNull_EvenForOtherwiseValidToken()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key), enabled: false);
        var token = CreateToken(key);

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task NoFrontendApiUrlConfigured_ReturnsNull()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(key), frontendApiUrl: "");
        var token = CreateToken(key);

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task JwksUnavailable_NoKeys_ReturnsNull()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new TestKeyProvider(/* no keys */));
        var token = CreateToken(key);

        Assert.Null(await verifier.VerifyAsync(token));
    }

    [Fact]
    public async Task KeyProviderThrows_FailsClosed_ReturnsNull()
    {
        var key = NewKey("kid-1");
        var verifier = BuildVerifier(new ThrowingKeyProvider());
        var token = CreateToken(key);

        Assert.Null(await verifier.VerifyAsync(token));
    }

    private sealed class ThrowingKeyProvider : IClerkSigningKeyProvider
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
            => throw new HttpRequestException("simulated JWKS outage");
    }
}
