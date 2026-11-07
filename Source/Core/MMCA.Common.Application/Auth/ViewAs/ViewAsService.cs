using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// The framework's <see cref="IViewAsService"/> (ADR-137).
/// </summary>
/// <remarks>
/// <para>
/// <b>Minted by the token service directly.</b> The injected <see cref="ITokenService"/> is the raw
/// signer, not the session-stamping wrapper the sign-in workflow mints through, so a view-as token
/// carries no <c>sid</c> and no <c>mfa</c> claim and no refresh session is opened for it. It carries
/// the viewed account's standard claims plus <c>act_sub</c> (the staff user) and <c>vag</c> (the
/// grant), and lives min(15 minutes, the configured access-token lifetime). Nothing renews it.
/// </para>
/// <para>
/// <b>No lateral access.</b> An account whose role holds <c>users:view-as</c> is never viewed, so
/// staff cannot view each other.
/// </para>
/// </remarks>
/// <param name="grants">The grant store.</param>
/// <param name="tokenService">The raw token signer.</param>
/// <param name="permissions">The host's permission registry, read for the no-lateral-access rule.</param>
/// <param name="settings">The view-as settings (retention).</param>
/// <param name="timeProvider">The clock grants are stamped from.</param>
/// <param name="subjects">The consumer's account reader; without one every grant is refused.</param>
public sealed class ViewAsService(
    IViewAsGrantStore grants,
    ITokenService tokenService,
    IPermissionRegistry permissions,
    IOptions<ViewAsSettings> settings,
    TimeProvider timeProvider,
    IViewAsSubjectResolver? subjects = null) : IViewAsService
{
    /// <summary>
    /// The grant's lifetime: min(15 minutes, the configured access-token lifetime), so a view never
    /// outlives what an ordinary sign-in token would.
    /// </summary>
    private TimeSpan Lifetime =>
        tokenService.AccessTokenLifetime > TimeSpan.Zero && tokenService.AccessTokenLifetime < ViewAsGrant.MaxLifetime
            ? tokenService.AccessTokenLifetime
            : ViewAsGrant.MaxLifetime;

    /// <inheritdoc />
    /// <remarks>
    /// The grant row is written before the token is minted, so no token ever exists without its audit
    /// record. Each grant also sweeps rows past <see cref="ViewAsSettings.RetentionDays"/>: grants are
    /// rare, so the sweep rides on them instead of on a hosted service in every process.
    /// </remarks>
    public async Task<Result<ViewAsGrantResponse>> StartAsync(
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lifetime = Lifetime;

        // Self and the reason are checked before any lookup.
        var created = ViewAsGrant.Create(staffUserId, targetUserId, reason, now, lifetime);
        if (!created.TryGetValue(out var grant))
        {
            return Result.Failure<ViewAsGrantResponse>(created.Errors);
        }

        if (subjects is null)
        {
            return Result.Failure<ViewAsGrantResponse>(Error.Failure(
                "ViewAs.NotConfigured",
                "View-as is enabled but the host registered no IViewAsSubjectResolver."));
        }

        var subject = await subjects.FindAsync(targetUserId, cancellationToken).ConfigureAwait(false);
        var refusal = Refuse(subject);
        if (refusal is not null)
        {
            return Result.Failure<ViewAsGrantResponse>(refusal);
        }

        await grants.DeleteIssuedBeforeAsync(now.AddDays(-settings.Value.RetentionDays), cancellationToken).ConfigureAwait(false);
        await grants.AddAsync(grant, cancellationToken).ConfigureAwait(false);

        Claim[] viewAsClaims =
        [
            new(AuthClaimTypes.ViewAsActor, staffUserId.ToString(CultureInfo.InvariantCulture)),
            new(AuthClaimTypes.ViewAsGrant, grant.Id.ToString()),
        ];
        var token = tokenService.GenerateShortLivedAccessToken(
            subject!.UserId,
            subject.Email,
            subject.Role,
            subject.FullName,
            viewAsClaims,
            lifetime);

        return Result.Success(new ViewAsGrantResponse(token, grant.ExpiresAt, grant.Id, targetUserId));
    }

    /// <inheritdoc />
    public async Task<Result> EndAsync(Guid grantId, UserIdentifierType staffUserId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var found = await grants.EndAsync(grantId, staffUserId, now, cancellationToken).ConfigureAwait(false);

        return found
            ? Result.Success()
            : Result.Failure(Error.NotFoundError("ViewAs.GrantNotFound", "No such view-as grant."));
    }

    /// <inheritdoc />
    public async Task<bool> IsGrantActiveAsync(
        Guid grantId,
        UserIdentifierType staffUserId,
        UserIdentifierType targetUserId,
        CancellationToken cancellationToken = default)
    {
        var grant = await grants.FindAsync(grantId, cancellationToken).ConfigureAwait(false);

        return grant is not null
            && grant.StaffUserId == staffUserId
            && grant.TargetUserId == targetUserId
            && grant.IsActiveAt(timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>The refusal for a resolved account, or <see langword="null"/> when it may be viewed.</summary>
    private Error? Refuse(ViewAsSubject? subject)
    {
        if (subject is null)
        {
            return Error.NotFoundError("ViewAs.TargetNotFound", "No such account.");
        }

        if (subject.IsDeleted)
        {
            return Error.Validation("ViewAs.TargetDeleted", "A deleted account cannot be viewed.");
        }

        // No lateral access: an account that may itself view others is never viewed.
        return permissions.GetPermissions(subject.Role).Contains(AdministrationPermissions.ViewAsUsers)
            ? Error.Forbidden("ViewAs.TargetIsStaff", "An account that can itself view others cannot be viewed.")
            : null;
    }
}
