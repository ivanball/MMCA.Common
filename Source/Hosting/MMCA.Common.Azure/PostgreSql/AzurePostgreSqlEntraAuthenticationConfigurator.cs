using Azure.Identity;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Azure.PostgreSql;

/// <summary>
/// Puts every PostgreSQL data source on Microsoft Entra token login for Azure Database for PostgreSQL:
/// the password becomes an Entra access token for the <c>ossrdbms-aad</c> scope from the token source,
/// handed to Npgsql through a periodic password provider and retried after 30 seconds when the request
/// fails. The token is a bearer credential sent as the password, so a connection string that does not
/// require TLS is rejected rather than allowed to send it in clear.
/// </summary>
/// <remarks>
/// The refresh interval is four minutes, not a fraction of the token lifetime (5 to 60 minutes),
/// because the bound is the credential's cache: Azure.Identity hands out its cached token until fewer
/// than five minutes remain and then fetches a new one, while Npgsql reuses whatever it was handed for
/// the whole interval. An interval under five minutes keeps every token Npgsql holds valid until its
/// next refresh; each refresh is an in-memory read of the cached token.
/// </remarks>
internal sealed class AzurePostgreSqlEntraAuthenticationConfigurator : INpgsqlDataSourceConfigurator
{
    /// <summary>How often Npgsql asks for the current token: inside the credential's refresh margin.</summary>
    internal static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(4);

    /// <summary>How soon a failed token request is retried.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<string>> _tokenSource;

    /// <summary>Initializes a new instance over <see cref="DefaultAzureCredential"/>.</summary>
    public AzurePostgreSqlEntraAuthenticationConfigurator()
        : this(new AzurePostgreSqlAccessTokenSource(new DefaultAzureCredential()).GetTokenAsync)
    {
    }

    /// <summary>Initializes a new instance over an explicit token source (tests).</summary>
    /// <param name="tokenSource">Returns the access token to send as the password.</param>
    internal AzurePostgreSqlEntraAuthenticationConfigurator(Func<CancellationToken, Task<string>> tokenSource) =>
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

        foreach (var (configurationKey, connectionString) in candidates.Where(static c => !string.IsNullOrWhiteSpace(c.ConnectionString)))
        {
            // Parsed through the data-source builder, the same parser Configure sees.
            EnsureEncrypted(new NpgsqlDataSourceBuilder(connectionString).ConnectionStringBuilder, configurationKey);
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
            $"{AzurePostgreSqlEntraAuthenticationSettings.SectionPath}:Enabled is true, but {source} resolves to SSL Mode '{settings.SslMode}'. "
            + "A Microsoft Entra access token is sent as the password, so the connection must be encrypted: add 'SSL Mode=Require' "
            + $"(or VerifyCA / VerifyFull) to {source}.");
    }
}
