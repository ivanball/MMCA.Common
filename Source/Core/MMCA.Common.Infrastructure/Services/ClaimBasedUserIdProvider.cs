using Microsoft.AspNetCore.SignalR;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Extracts the user identifier from the connection's <c>sub</c> claim (or the
/// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> form the bearer handler maps it to)
/// so that <see cref="IHubContext{THub}"/>.Clients.User(userId) routes to the correct connections.
/// </summary>
public sealed class ClaimBasedUserIdProvider : IUserIdProvider
{
    /// <inheritdoc />
    public string? GetUserId(HubConnectionContext connection) =>
        connection?.User.FindUserIdValue();
}
