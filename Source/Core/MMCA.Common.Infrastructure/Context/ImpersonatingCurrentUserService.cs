using System.Globalization;
using System.Security.Claims;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// Decorates whatever <see cref="ICurrentUserService"/> the host registered so a scope carrying a
/// <see cref="ScopedUserOverride"/> answers from that principal instead. With no override set every
/// member reads straight through to the inner service, so an HTTP request behaves exactly as it did
/// before this type existed.
/// <para>
/// Registered as a decorator rather than as a replacement: a host that registered its own
/// <see cref="ICurrentUserService"/> keeps it and simply gains the override behavior.
/// </para>
/// </summary>
/// <param name="inner">The registered current-user service, used whenever no override is set.</param>
/// <param name="userOverride">The scope's override carrier.</param>
internal sealed class ImpersonatingCurrentUserService(
    ICurrentUserService inner,
    ScopedUserOverride userOverride) : ICurrentUserService
{
    /// <inheritdoc />
    public ClaimsPrincipal User => userOverride.Principal ?? inner.User;

    /// <inheritdoc />
    public UserIdentifierType? UserId =>
        userOverride.Principal is { } principal ? principal.GetUserId() : inner.UserId;

    /// <inheritdoc />
    public string? Role =>
        userOverride.Principal is { } principal
            ? principal.FindFirst(ClaimTypes.Role)?.Value
            : inner.Role;

    /// <inheritdoc />
    public T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T>
    {
        if (userOverride.Principal is not { } principal)
        {
            return inner.GetClaimValue<T>(claimType);
        }

        var claim = principal.FindFirst(claimType)?.Value;

        // Claims are machine-written in the invariant culture, exactly as CurrentUserService reads
        // them; parsing under the ambient culture misreads separators for decimal and DateTime types.
        return claim is not null && T.TryParse(claim, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
