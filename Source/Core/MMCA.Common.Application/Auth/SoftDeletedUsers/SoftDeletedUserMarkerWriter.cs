using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;

namespace MMCA.Common.Application.Auth.SoftDeletedUsers;

/// <summary>
/// Writes the deleted-or-locked marker for <see cref="SoftDeletedUserCache.MarkerDurationFor"/> of the
/// host's access-token lifetime, and removes it on an unlock. The default bearer clock skew is assumed:
/// this layer cannot read the host's.
/// </summary>
/// <param name="cache">The shared cache the marker is written to.</param>
/// <param name="tokenService">Supplies the host's access-token lifetime.</param>
public sealed class SoftDeletedUserMarkerWriter(ICacheService cache, ITokenService tokenService) : ISoftDeletedUserMarkerWriter
{
    /// <inheritdoc />
    public Task MarkDeletedAsync(UserIdentifierType userId, CancellationToken cancellationToken) =>
        SoftDeletedUserCache.MarkDeletedForAsync(
            cache,
            userId,
            SoftDeletedUserCache.MarkerDurationFor(tokenService.AccessTokenLifetime),
            cancellationToken);

    /// <inheritdoc />
    public Task ClearMarkerAsync(UserIdentifierType userId, CancellationToken cancellationToken) =>
        SoftDeletedUserCache.ClearMarkerAsync(cache, userId, cancellationToken);
}
