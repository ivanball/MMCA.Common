using Amazon;
using Amazon.RDS.Util;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Aws.Rds;

/// <summary>
/// Puts every PostgreSQL data source on RDS IAM database authentication: the password becomes a
/// short-lived token (15-minute lifetime) that <see cref="RDSAuthTokenGenerator"/> signs locally with
/// SigV4 for the connection string's own host, port and user, refreshed every 10 minutes and retried
/// after 30 seconds when generation fails. The token is a bearer credential sent as the password, so
/// a connection string that does not require TLS is rejected rather than allowed to send it in clear.
/// </summary>
/// <param name="region">The signing region, or <see langword="null"/> for the AWS SDK region chain.</param>
/// <param name="credentials">Static credentials, or <see langword="null"/> for the AWS default credential chain.</param>
internal sealed class RdsIamAuthenticationConfigurator(RegionEndpoint? region, AWSCredentials? credentials)
    : INpgsqlDataSourceConfigurator
{
    /// <summary>How often a fresh token replaces the current one: well inside its 15-minute lifetime.</summary>
    internal static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>How soon a failed token generation is retried.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The connection string does not require TLS.</exception>
    public void Configure(NpgsqlDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureEncrypted(
            builder.ConnectionStringBuilder,
            $"the PostgreSQL connection string for host '{builder.ConnectionStringBuilder.Host}'");

        builder.UsePeriodicPasswordProvider(GenerateTokenAsync, TokenRefreshInterval, FailureRetryInterval);
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

    /// <summary>The periodic password provider: one signed connect token for this connection string.</summary>
    /// <param name="settings">The data source's connection string settings.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    /// <returns>The token to send as the password.</returns>
    internal async ValueTask<string> GenerateTokenAsync(NpgsqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var host = settings.Host;
        var user = settings.Username;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException(
                "RDS IAM authentication needs a Host and a Username on the PostgreSQL connection string: the token is signed for that endpoint and database user.");
        }

        var port = settings.Port;

        if (credentials is not null && region is not null)
        {
            return await RDSAuthTokenGenerator.GenerateAuthTokenAsync(credentials, region, host, port, user).ConfigureAwait(false);
        }

        if (credentials is not null)
        {
            return await RDSAuthTokenGenerator.GenerateAuthTokenAsync(credentials, host, port, user).ConfigureAwait(false);
        }

        if (region is not null)
        {
            return await RDSAuthTokenGenerator.GenerateAuthTokenAsync(region, host, port, user).ConfigureAwait(false);
        }

        return await RDSAuthTokenGenerator.GenerateAuthTokenAsync(host, port, user).ConfigureAwait(false);
    }

    private static void EnsureEncrypted(NpgsqlConnectionStringBuilder settings, string source)
    {
        if (settings.SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{RdsIamAuthenticationSettings.SectionPath}:Enabled is true, but {source} resolves to SSL Mode '{settings.SslMode}'. "
            + "An RDS IAM token is sent as the password, so the connection must be encrypted: add 'SSL Mode=Require' "
            + $"(or VerifyCA / VerifyFull, with the RDS CA bundle as the root certificate) to {source}.");
    }
}
