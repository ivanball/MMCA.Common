using Microsoft.AspNetCore.SignalR;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Extracts the user identifier from the <c>user_id</c> JWT claim so that
/// <see cref="IHubContext{THub}"/>.Clients.User(userId) routes to the correct connections.
/// </summary>
public sealed class ClaimBasedUserIdProvider : IUserIdProvider
{
    private const string UserIdClaimType = "user_id";

    /// <inheritdoc />
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(UserIdClaimType)?.Value;
}
