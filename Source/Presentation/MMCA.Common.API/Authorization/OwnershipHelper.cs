using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

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
    /// <remarks>
    /// Checks every role the caller holds through <see cref="ICurrentUserService.IsInRole"/>, the same
    /// check a controller makes inline. <see cref="ICurrentUserService.Role"/> is the first role claim
    /// only, so comparing against it alone treated a caller holding the bypass role second as a
    /// non-privileged user here while an inline check said otherwise.
    /// </remarks>
    public static bool IsAdmin(ICurrentUserService currentUserService, string bypassRole)
    {
        ArgumentNullException.ThrowIfNull(currentUserService);
        return currentUserService.IsInRole(bypassRole);
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

    /// <summary>
    /// Fail-closed gate for reads scoped through <see cref="GetOwnershipSpecification{TSpec, TId}"/>.
    /// That method returns <see langword="null"/> for two different reasons: the caller holds the
    /// bypass role (scoping deliberately skipped) or the caller is a non-privileged user whose owner
    /// claim cannot be resolved. Only the first may read unscoped, so call this before the read and
    /// return its failure unchanged; a missing claim is an authorization answer (403), never an
    /// unscoped read and never a 500.
    /// </summary>
    /// <typeparam name="TId">The owner claim's value type (e.g., <see langword="int"/> or <see cref="Guid"/>).</typeparam>
    /// <param name="currentUserService">The current user service to read the role and claim from.</param>
    /// <param name="claimType">The owner claim the host scopes by (e.g., <c>customer_id</c>).</param>
    /// <param name="bypassRole">The role exempt from scoping, named by the host. Checked with <see cref="IsAdmin"/>, exactly as <see cref="GetOwnershipSpecification{TSpec, TId}"/> checks it, so the gate and the scope agree on who is privileged.</param>
    /// <param name="source">The error source to report (typically the controller name).</param>
    /// <param name="target">The error target to report (typically the entity name).</param>
    /// <returns>
    /// Success when the caller holds <paramref name="bypassRole"/> or carries a parsable
    /// <paramref name="claimType"/>; otherwise a <see cref="ErrorType.Forbidden"/> failure
    /// (<c>Error.Forbidden</c>, "Access denied.") carrying <paramref name="source"/> and <paramref name="target"/>.
    /// </returns>
    public static Result RequireResolvableOwner<TId>(
        ICurrentUserService currentUserService,
        string claimType,
        string bypassRole,
        string source,
        string target)
        where TId : struct, IParsable<TId>
    {
        ArgumentNullException.ThrowIfNull(currentUserService);

        return IsAdmin(currentUserService, bypassRole) || currentUserService.GetClaimValue<TId>(claimType) is not null
            ? Result.Success()
            : Result.Failure(AccessDenied(source, target));
    }

    /// <summary>
    /// Owner-or-bypass gate for an action on one record: the caller holding the bypass role passes
    /// without a lookup; anyone else must resolve to an owner AND own the record.
    /// </summary>
    /// <remarks>
    /// Fail-closed in the same way as <see cref="RequireResolvableOwner{TId}"/>: an unresolvable owner
    /// claim is refused with 403 and the ownership lookup never runs. A resolvable caller who does not
    /// own the record is answered 404, not 403, so the existence of another owner's record cannot be
    /// probed for.
    /// </remarks>
    /// <typeparam name="TId">The owner claim's value type (e.g., <see langword="int"/> or <see cref="Guid"/>).</typeparam>
    /// <param name="currentUserService">The current user service to read the role and claim from.</param>
    /// <param name="claimType">The owner claim the host scopes by (e.g., <c>customer_id</c>).</param>
    /// <param name="bypassRole">The role exempt from the check, named by the host (checked with <see cref="IsAdmin"/>).</param>
    /// <param name="isOwnedBy">
    /// Answers whether the record belongs to the resolved owner, typically an
    /// <c>ExistsAsync(r =&gt; r.Id == id &amp;&amp; r.OwnerId == ownerId)</c> query. Receives the
    /// resolved owner and <paramref name="cancellationToken"/>.
    /// </param>
    /// <param name="source">The error source to report (typically the controller name).</param>
    /// <param name="target">The error target to report (typically the entity name).</param>
    /// <param name="cancellationToken">Cancellation token passed to <paramref name="isOwnedBy"/>.</param>
    /// <returns>
    /// Success for the bypass role or the owner; a <see cref="ErrorType.Forbidden"/> failure when the
    /// owner claim cannot be resolved; <c>Error.NotFound</c> with <paramref name="source"/> and
    /// <paramref name="target"/> when the caller does not own the record.
    /// </returns>
    public static Task<Result> ValidateOwnershipAsync<TId>(
        ICurrentUserService currentUserService,
        string claimType,
        string bypassRole,
        Func<TId, CancellationToken, Task<bool>> isOwnedBy,
        string source,
        string target,
        CancellationToken cancellationToken)
        where TId : struct, IParsable<TId>
    {
        ArgumentNullException.ThrowIfNull(currentUserService);
        ArgumentNullException.ThrowIfNull(isOwnedBy);

        if (IsAdmin(currentUserService, bypassRole))
        {
            return Task.FromResult(Result.Success());
        }

        if (currentUserService.GetClaimValue<TId>(claimType) is not { } ownerId)
        {
            return Task.FromResult(Result.Failure(AccessDenied(source, target)));
        }

        return CheckOwnershipAsync(isOwnedBy, ownerId, source, target, cancellationToken);
    }

    private static async Task<Result> CheckOwnershipAsync<TId>(
        Func<TId, CancellationToken, Task<bool>> isOwnedBy,
        TId ownerId,
        string source,
        string target,
        CancellationToken cancellationToken)
        where TId : struct, IParsable<TId>
    {
        var isOwner = await isOwnedBy(ownerId, cancellationToken).ConfigureAwait(false);

        return isOwner
            ? Result.Success()
            : Result.Failure(Error.NotFound.WithSource(source).WithTarget(target));
    }

    private static Error AccessDenied(string source, string target) =>
        Error.Forbidden("Error.Forbidden", "Access denied.", source, target);
}
