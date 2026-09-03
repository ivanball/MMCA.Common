using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// Extracts the current user's identity from JWT claims in the HTTP context. The user id rides the
/// standard <c>sub</c> claim <see cref="TokenService"/> emits, read through
/// <see cref="ClaimsPrincipalExtensions"/> so the <see cref="ClaimTypes.NameIdentifier"/> form the
/// bearer handler maps it to resolves identically.
/// Claim values are cached per request via <see cref="Lazy{T}"/> since the service is scoped.
/// </summary>
public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    private readonly Lazy<UserIdentifierType?> _userId = new(() =>
        httpContextAccessor.HttpContext?.User.GetUserId());

    private readonly Lazy<string?> _role = new(() =>
        httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Role)?.Value);

    /// <inheritdoc />
    public ClaimsPrincipal User => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();

    /// <inheritdoc />
    public UserIdentifierType? UserId => _userId.Value;

    /// <inheritdoc />
    public string? Role => _role.Value;

    /// <inheritdoc />
    public T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T>
    {
        var claim = httpContextAccessor.HttpContext?.User?.FindFirst(claimType)?.Value;
        // Claims are machine-written in the invariant culture; parsing them under the ambient
        // request culture misreads separators for decimal, double and DateTime claim types.
        return claim is not null && T.TryParse(claim, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
