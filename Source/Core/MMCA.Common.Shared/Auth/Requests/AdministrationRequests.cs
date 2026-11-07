using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for replacing the set of roles an account holds.
/// </summary>
/// <remarks>
/// A replace rather than an add/remove pair: the administration UI edits a list, and a replace makes
/// the request idempotent (re-submitting the same list is a no-op) where an add/remove pair would
/// need the caller to know what the account already had.
/// </remarks>
/// <param name="Roles">The complete set of roles the account should hold afterwards.</param>
public sealed record SetUserRolesRequest(IReadOnlyList<string> Roles);

/// <summary>
/// Request payload for replacing the stored permission grants of one role.
/// </summary>
/// <remarks>
/// These are the STORED grants only. Permissions compiled into the host's
/// <c>IPermissionRegistry</c> are not editable and are not returned here, so an operator can never
/// remove a capability the code depends on by editing data.
/// </remarks>
/// <param name="Permissions">The complete set of stored permissions the role should grant afterwards.</param>
public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions);

/// <summary>
/// Request payload for starting a read-only view-as session on another account (ADR-137).
/// </summary>
/// <param name="Reason">
/// Why the staff user needs to see the account, in free text (10 to 500 characters). Stored on the
/// grant row, which is the audit record of the access.
/// </param>
public sealed record StartViewAsRequest(string? Reason);

/// <summary>
/// One page of the support access log (ADR-137), with its filters. Every filter is optional and they
/// combine with AND; the rows come newest first.
/// </summary>
public sealed record ViewAsGrantListRequest
{
    /// <summary>The page size used when the caller names none.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The largest page size served; a larger request is clamped to it.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Gets text the staff user's email must contain (case-insensitive), or <see langword="null"/>.</summary>
    public string? Staff { get; init; }

    /// <summary>Gets text the viewed account's email must contain (case-insensitive), or <see langword="null"/>.</summary>
    public string? Viewed { get; init; }

    /// <summary>Gets the earliest grant instant included (UTC, inclusive), or <see langword="null"/>.</summary>
    public DateTime? From { get; init; }

    /// <summary>Gets the instant grants must precede (UTC, exclusive), or <see langword="null"/>.</summary>
    public DateTime? To { get; init; }

    /// <summary>Gets the status grants must have at the time of the read, or <see langword="null"/> for any.</summary>
    public ViewAsGrantStatus? Status { get; init; }

    /// <summary>Gets the 1-based page number. Values below 1 read page 1.</summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>Gets the page size, clamped into 1 to <see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}
