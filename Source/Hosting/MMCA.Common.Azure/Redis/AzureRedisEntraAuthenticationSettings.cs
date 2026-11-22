namespace MMCA.Common.Azure.Redis;

/// <summary>
/// Microsoft Entra token login for Azure Cache for Redis and Azure Managed Redis, bound from
/// <c>AzureRedis:EntraAuthentication</c>. The connection string keeps the host, port and TLS settings
/// and carries no access key; the identity whose token is sent is the managed identity named by
/// <see cref="ManagedIdentityClientId"/>, or whatever <c>DefaultAzureCredential</c> resolves to when it
/// is blank.
/// </summary>
internal sealed class AzureRedisEntraAuthenticationSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "AzureRedis:EntraAuthentication";

    /// <summary>Switches every framework Redis connection onto Entra access tokens. Off when absent.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The client id of the user-assigned managed identity to sign in as. Blank selects the
    /// system-assigned identity (or the one <c>AZURE_CLIENT_ID</c> names).
    /// </summary>
    public string? ManagedIdentityClientId { get; init; }
}
