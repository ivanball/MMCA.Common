using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Gcp.CloudSql;

/// <summary>
/// Puts every PostgreSQL data source on Cloud SQL IAM database authentication: the password becomes an
/// OAuth2 access token for the <c>sqlservice.login</c> scope (one-hour lifetime) from the token source,
/// handed to Npgsql through a periodic password provider and retried after 30 seconds when the request
/// fails. The token is a bearer credential sent as the password, so a connection string that does not
/// require TLS is rejected rather than allowed to send it in clear.
/// </summary>
/// <remarks>
/// The refresh interval is one minute, not a fraction of the hour, because the bound is the
/// credential's cache rather than the token lifetime: Google's credential hands out its cached token
/// until fewer than 225 seconds remain (refreshing in the background) and blocks for a new one only
/// under 60, while Npgsql reuses whatever it was handed for the whole interval. A one-minute interval
/// keeps every token Npgsql holds valid until its next refresh; each refresh is an in-memory read of
/// the cached token, so the short interval costs nothing.
/// </remarks>
internal sealed class CloudSqlIamAuthenticationConfigurator : INpgsqlDataSourceConfigurator
{
    /// <summary>How often Npgsql asks for the current token: inside the credential's refresh window.</summary>
    internal static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>How soon a failed token request is retried.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<string>> _tokenSource;

    /// <summary>Initializes a new instance over Application Default Credentials.</summary>
    public CloudSqlIamAuthenticationConfigurator()
        : this(new CloudSqlAccessTokenSource().GetTokenAsync)
    {
    }

    /// <summary>Initializes a new instance over an explicit token source (tests).</summary>
    /// <param name="tokenSource">Returns the access token to send as the password.</param>
    internal CloudSqlIamAuthenticationConfigurator(Func<CancellationToken, Task<string>> tokenSource) =>
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The connection string does not require TLS.</exception>
    public void Configure(NpgsqlDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureEncrypted(
            builder.ConnectionStringBuilder,
            $"the PostgreSQL connection string for host '{builder.ConnectionStringBuilder.Host}'");

        builder.UsePeriodicPasswordProvider(GetPasswordAsync, TokenRefreshInterval, FailureRetryInterval);
    }

    /// <summary>
    /// Fails registration for every PostgreSQL connection string already in configuration that does not
    /// require TLS, naming its key: the top-level <c>ConnectionStrings</c> entry and each named
    /// <c>DataSources</c> entry. A connection string supplied later is still checked by
    /// <see cref="Configure"/> when its data source is first built.
    /// </summary>
    /// <param name="configuration">Application configuration carrying the connection strings.</param>
    /// <exception cref="InvalidOperationException">A PostgreSQL connection string does not require TLS.</exception>
    internal static void EnsureConfiguredConnectionStringsAreEncrypted(IConfiguration configuration)
    {
        const string key = nameof(ConnectionStringSettings.PostgreSQLConnectionString);

        List<(string Key, string? ConnectionString)> candidates =
        [
            ($"{ConnectionStringSettings.SectionName}:{key}", configuration.GetSection(ConnectionStringSettings.SectionName)[key]),
            .. configuration.GetSection(DataSourcesSettings.SectionName).GetChildren()
                .Select(entry => ($"{DataSourcesSettings.SectionName}:{entry.Key}:{key}", entry[key])),
        ];

        foreach (var (configurationKey, connectionString) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                // Parsed through the data-source builder, the same parser Configure sees.
                EnsureEncrypted(new NpgsqlDataSourceBuilder(connectionString).ConnectionStringBuilder, configurationKey);
            }
        }
    }

    /// <summary>The periodic password provider: the current access token.</summary>
    /// <param name="settings">The data source's connection string settings (the token does not depend on them).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The token to send as the password.</returns>
    internal async ValueTask<string> GetPasswordAsync(NpgsqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        return await _tokenSource(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureEncrypted(NpgsqlConnectionStringBuilder settings, string source)
    {
        if (settings.SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{CloudSqlIamAuthenticationSettings.SectionPath}:Enabled is true, but {source} resolves to SSL Mode '{settings.SslMode}'. "
            + "A Cloud SQL IAM token is sent as the password, so the connection must be encrypted: add 'SSL Mode=Require' "
            + $"(or VerifyCA / VerifyFull, with the instance's server CA as the root certificate) to {source}.");
    }
}
