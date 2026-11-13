using Azure.Core;

namespace MMCA.Common.Azure.PostgreSql;

/// <summary>
/// The default token source behind <see cref="AzurePostgreSqlEntraAuthenticationConfigurator"/>: a
/// Microsoft Entra access token for the Azure Database for PostgreSQL scope. The credential is created
/// with the configurator, but no token is requested until the first connection; the credential caches
/// its token and refreshes it itself, so each call is an in-memory read until the token nears its expiry.
/// </summary>
/// <param name="credential">The credential the token is requested from.</param>
internal sealed class AzurePostgreSqlAccessTokenSource(TokenCredential credential)
{
    /// <summary>The scope Azure Database for PostgreSQL accepts for Entra login.</summary>
    internal const string Scope = "https://ossrdbms-aad.database.windows.net/.default";

    private readonly TokenCredential _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Returns a current access token for <see cref="Scope"/>.</summary>
    /// <param name="cancellationToken">Cancels the token request.</param>
    /// <returns>The access token, sent to the server as the password.</returns>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken)
            .ConfigureAwait(false);
        return token.Token;
    }
}
