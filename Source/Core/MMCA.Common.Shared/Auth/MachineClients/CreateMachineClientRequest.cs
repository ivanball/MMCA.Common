namespace MMCA.Common.Shared.Auth.MachineClients;

/// <summary>
/// Creates a machine client and the service-account user behind it (ADR-140).
/// </summary>
/// <param name="Name">The label the operator recognizes the client by (1 to 100 characters).</param>
/// <param name="Role">
/// The role the service account is created with; its permissions are what the client's tokens carry.
/// A role granting <c>users:view-as</c> or <c>users:manage</c> is refused.
/// </param>
/// <param name="SecretExpiresOn">
/// The UTC instant the client's secrets stop working, or <see langword="null"/> for no expiry.
/// </param>
public sealed record CreateMachineClientRequest(string Name, string Role, DateTime? SecretExpiresOn = null);
