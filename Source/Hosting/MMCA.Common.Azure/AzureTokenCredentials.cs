using Azure.Core;
using Azure.Identity;

namespace MMCA.Common.Azure;

/// <summary>
/// The token credential behind the adapter's Entra sign-ins that take an optional managed identity
/// client id from configuration (Redis, Service Bus): <see cref="DefaultAzureCredential"/>, so a
/// managed identity signs in on Azure and a developer sign-in works locally, pinned to the
/// user-assigned identity whose client id is configured.
/// </summary>
internal static class AzureTokenCredentials
{
    /// <summary>Creates the credential for an optional user-assigned managed identity.</summary>
    /// <param name="managedIdentityClientId">
    /// The client id of a user-assigned managed identity, or blank for the system-assigned identity
    /// (or whatever <c>AZURE_CLIENT_ID</c> selects).
    /// </param>
    /// <returns>The credential.</returns>
    internal static TokenCredential Create(string? managedIdentityClientId) =>
        string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId.Trim() });
}
