using Google.Apis.Auth.OAuth2;

namespace MMCA.Common.Gcp.CloudSql;

/// <summary>
/// The default token source behind <see cref="CloudSqlIamAuthenticationConfigurator"/>: an OAuth2
/// access token for the Cloud SQL login scope from Application Default Credentials. The credential is
/// loaded on the first request, not at registration, so a host builds its container without Google
/// credentials and only the first database connection needs them. The credential caches its token and
/// refreshes it itself, so each call is an in-memory read until the token nears its expiry.
/// </summary>
internal sealed class CloudSqlAccessTokenSource
{
    /// <summary>The OAuth2 scope Cloud SQL accepts for IAM database login.</summary>
    internal const string Scope = "https://www.googleapis.com/auth/sqlservice.login";

    private ITokenAccess? _credential;

    /// <summary>Returns a current access token for <see cref="Scope"/>.</summary>
    /// <param name="cancellationToken">Cancels the credential load or the token request.</param>
    /// <returns>The access token, sent to Cloud SQL as the password.</returns>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        // A race between two first callers loads the credential twice and keeps the last one, which is
        // harmless; a failed load is not cached, so the next call (Npgsql retries after its failure
        // interval) tries again.
        var credential = _credential;
        if (credential is null)
        {
            credential = (await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false))
                .CreateScoped(Scope);
            _credential = credential;
        }

        return await credential.GetAccessTokenForRequestAsync(authUri: null, cancellationToken).ConfigureAwait(false);
    }
}
