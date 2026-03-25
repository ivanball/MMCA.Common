using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Extracts the current user's identity from JWT claims in the HTTP context.
/// Uses custom claim types (<c>user_id</c>) matching those emitted by <see cref="TokenService"/>.
/// Claim values are cached per request via <see cref="Lazy{T}"/> since the service is scoped.
/// </summary>
public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    /// <summary>Custom claim type for the internal user ID (not the standard "sub" claim).</summary>
    private const string UserIdClaimType = "user_id";

    private readonly Lazy<UserIdentifierType?> _userId = new(() =>
    {
        var claim = httpContextAccessor.HttpContext?.User?.FindFirst(UserIdClaimType)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    });

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
        return claim is not null && T.TryParse(claim, null, out var value) ? value : null;
    }
}
