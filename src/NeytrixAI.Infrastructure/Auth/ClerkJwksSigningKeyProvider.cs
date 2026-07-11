using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NeytrixAI.Infrastructure.Auth;

/// <summary>
/// Fetches Clerk's public signing keys from the tenant's OpenID Connect metadata
/// (<c>{FrontendApiUrl}/.well-known/openid-configuration</c> → <c>jwks_uri</c>).
///
/// Uses <see cref="ConfigurationManager{T}"/> which caches the document and
/// transparently refreshes it (handling Clerk's key rotation) so we neither hit
/// the network on every request nor go stale after a rotation. Network/parse
/// failures surface as exceptions here and are turned into a fail-closed
/// "anonymous" result by <see cref="ClerkTokenVerifier"/>.
/// </summary>
public sealed class ClerkJwksSigningKeyProvider : IClerkSigningKeyProvider
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;

    public ClerkJwksSigningKeyProvider(IOptions<ClerkOptions> options)
    {
        var frontendApiUrl = options.Value.FrontendApiUrl;
        if (string.IsNullOrWhiteSpace(frontendApiUrl))
        {
            // Not configured — GetSigningKeysAsync returns an empty set and every
            // token fails verification (fail closed to anonymous).
            _configurationManager = null;
            return;
        }

        var metadataAddress = $"{frontendApiUrl.TrimEnd('/')}/.well-known/openid-configuration";
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_configurationManager is null)
            return Array.Empty<SecurityKey>();

        var config = await _configurationManager.GetConfigurationAsync(cancellationToken);
        return config.SigningKeys.ToArray();
    }
}
