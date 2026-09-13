using MMCA.Common.Application.Interfaces.Infrastructure.Auth;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Utility methods for ownership-based authorization in controllers.
/// Helps controllers create specification objects that scope queries to the current user's data
/// when the user does not hold the privileged bypass role.
/// </summary>
public static class OwnershipHelper
{
    /// <summary>
    /// Returns <see langword="true"/> if the current user holds the privileged bypass role. The role
    /// is always supplied by the caller, normally from
    /// <see cref="OwnerOrAdminFilterOptions.BypassRole"/>: the framework declares no role names.
    /// </summary>
    public static bool IsAdmin(ICurrentUserService currentUserService, string bypassRole)
    {
        ArgumentNullException.ThrowIfNull(currentUserService);
        return string.Equals(currentUserService.Role, bypassRole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a specification that scopes queries to the current user's data,
    /// or <see langword="null"/> if the user holds the bypass role (no scoping needed).
    /// </summary>
    /// <typeparam name="TSpec">The specification type, typically scoping by owner ID.</typeparam>
    /// <typeparam name="TId">The identifier type for the owner claim (e.g., <see langword="int"/>).</typeparam>
    /// <param name="currentUserService">The current user service to extract claims from.</param>
    /// <param name="claimType">The claim type name to look up (e.g., <c>"customer_id"</c>).</param>
    /// <param name="specFactory">Factory that creates the specification from an owner ID.</param>
    /// <param name="bypassRole">The role exempt from scoping, named by the host.</param>
    /// <returns>The specification instance, or <see langword="null"/> for privileged users.</returns>
    public static TSpec? GetOwnershipSpecification<TSpec, TId>(
        ICurrentUserService currentUserService,
        string claimType,
        Func<TId, TSpec> specFactory,
        string bypassRole)
        where TSpec : class
        where TId : struct, IParsable<TId>
    {
        ArgumentNullException.ThrowIfNull(currentUserService);
        ArgumentNullException.ThrowIfNull(specFactory);

        if (IsAdmin(currentUserService, bypassRole))
        {
            return null;
        }

        var id = currentUserService.GetClaimValue<TId>(claimType);
        return id.HasValue ? specFactory(id.Value) : null;
    }

    /// <summary>
    /// Returns a specification that scopes queries to the current user's customer data,
    /// or <see langword="null"/> if the user holds the bypass role (no scoping needed).
    /// Uses the <c>customer_id</c> claim.
    /// </summary>
    /// <typeparam name="TSpec">The specification type, typically scoping by customer ID.</typeparam>
    /// <param name="currentUserService">The current user service to extract claims from.</param>
    /// <param name="specFactory">Factory that creates the specification from a customer ID.</param>
    /// <param name="bypassRole">The role exempt from scoping, named by the host.</param>
    /// <returns>The specification instance, or <see langword="null"/> for privileged users.</returns>
    public static TSpec? GetOwnershipSpecification<TSpec>(
        ICurrentUserService currentUserService,
        Func<int, TSpec> specFactory,
        string bypassRole)
        where TSpec : class
        => GetOwnershipSpecification<TSpec, int>(currentUserService, "customer_id", specFactory, bypassRole);
}
