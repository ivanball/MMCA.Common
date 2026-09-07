using System.Security.Claims;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

/// <summary>
/// Decides whether a caller may subscribe to a live channel.
/// <para>
/// SECURITY: the notification hub validates only the SHAPE of a channel key, so without an
/// authorizer any authenticated principal can join any well-formed channel and receive everything
/// published to it (live poll results, session-scoped announcements) for as long as the connection
/// lasts. A host that publishes anything to a channel that is not public to every signed-in user
/// MUST register an implementation of this interface; with none registered the hub keeps its
/// historical behaviour and admits any authenticated caller.
/// </para>
/// </summary>
public interface IChannelJoinAuthorizer
{
    /// <summary>
    /// Whether <paramref name="user"/> may join <paramref name="channelKey"/>. The key has already
    /// passed the configured shape pattern.
    /// </summary>
    /// <param name="user">The connection's principal (authenticated: the hub requires it).</param>
    /// <param name="channelKey">The channel the caller asked to join, e.g. <c>event:42</c>.</param>
    /// <param name="cancellationToken">The connection's cancellation token.</param>
    /// <returns><see langword="true"/> to admit the connection to the channel.</returns>
    ValueTask<bool> CanJoinAsync(
        ClaimsPrincipal? user,
        string channelKey,
        CancellationToken cancellationToken = default);
}
