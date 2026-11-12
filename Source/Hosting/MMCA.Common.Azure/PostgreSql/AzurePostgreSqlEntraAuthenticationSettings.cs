namespace MMCA.Common.Azure.PostgreSql;

/// <summary>
/// Microsoft Entra token login for Azure Database for PostgreSQL, bound from
/// <c>AzurePostgreSql:EntraAuthentication</c>. <see cref="Enabled"/> is the only key: the database
/// user is the connection string's <c>Username</c> (the Entra principal name), and the identity whose
/// token is sent is whatever <c>DefaultAzureCredential</c> resolves to (a managed identity, selected
/// through <c>AZURE_CLIENT_ID</c> when it is user-assigned).
/// </summary>
internal sealed class AzurePostgreSqlEntraAuthenticationSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "AzurePostgreSql:EntraAuthentication";

    /// <summary>Switches PostgreSQL connections onto Entra access tokens. Off when absent.</summary>
    public bool Enabled { get; init; }
}
