using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Domain.FeatureFlags;

/// <summary>
/// One stored override of a declared feature flag (ADR-031): the data half of a reloadable flag,
/// laid over the value configuration gives it so an operator can flip a kill switch without a new
/// revision.
/// <para>
/// <b>Keyed by the flag name.</b> There is at most one override per flag in the owning service's
/// database, so the name IS the identity. Clearing an override soft-deletes the row rather than
/// removing it, so the audit stamps keep who cleared it and when; setting the flag again revives the
/// same row.
/// </para>
/// <para>
/// <b>Audited through the base.</b> It derives from <see cref="AuditableBaseEntity{TIdentifierType}"/>,
/// so every write is stamped by the audit interceptor with its author and instant, and carries the
/// row-version concurrency token, exactly like any aggregate a module owns. The required
/// <see cref="Reason"/> is the half of the audit no interceptor can supply.
/// </para>
/// </summary>
public sealed class FeatureFlagOverride : AuditableBaseEntity<string>
{
    /// <summary>Column width for the flag name.</summary>
    public const int NameMaxLength = 128;

    /// <summary>The longest reason accepted, and the column width.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>The longest single user or group name accepted.</summary>
    public const int TargetMaxLength = 128;

    /// <summary>Column width of a stored target list (the names joined by <see cref="TargetSeparator"/>).</summary>
    public const int TargetListMaxLength = 4000;

    /// <summary>The highest percentage a rollout may name.</summary>
    public const int MaxPercentage = 100;

    /// <summary>Separates the names in a stored target list, and is therefore refused inside one.</summary>
    public const char TargetSeparator = ',';

    private FeatureFlagOverride()
    {
    }

    /// <summary>Gets the name of the flag this override applies to (the identity).</summary>
    public string Name => Id;

    /// <summary>Gets what the override sets the flag to.</summary>
    public FeatureFlagOverrideMode Mode { get; private set; }

    /// <summary>Gets the rollout percentage, when <see cref="Mode"/> carries one.</summary>
    public int? Percentage { get; private set; }

    /// <summary>Gets the targeted user identifiers as stored (joined by <see cref="TargetSeparator"/>).</summary>
    public string? TargetUsers { get; private set; }

    /// <summary>Gets the targeted groups as stored (joined by <see cref="TargetSeparator"/>).</summary>
    public string? TargetGroups { get; private set; }

    /// <summary>Gets why the override exists.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Gets the targeted user identifiers.</summary>
    public IReadOnlyList<string> Users => Split(TargetUsers);

    /// <summary>Gets the targeted groups.</summary>
    public IReadOnlyList<string> Groups => Split(TargetGroups);

    /// <summary>
    /// Creates an override for one flag, validating the name, the reason and the definition.
    /// </summary>
    /// <param name="name">The flag name (trimmed; at most <see cref="NameMaxLength"/> characters).</param>
    /// <param name="mode">What the override sets the flag to.</param>
    /// <param name="reason">Why the override exists (required; at most <see cref="ReasonMaxLength"/> characters).</param>
    /// <param name="percentage">The rollout percentage; required for a percentage override.</param>
    /// <param name="users">The targeted user identifiers.</param>
    /// <param name="groups">The targeted groups.</param>
    /// <returns>The override, or a validation failure.</returns>
    public static Result<FeatureFlagOverride> Create(
        string name,
        FeatureFlagOverrideMode mode,
        string reason,
        int? percentage = null,
        IEnumerable<string>? users = null,
        IEnumerable<string>? groups = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result.Failure<FeatureFlagOverride>(Error.Validation(
                "FeatureFlagOverride.NameInvalid",
                "A feature flag override requires a flag name of at most 128 characters.",
                nameof(Create)));
        }

        var entity = new FeatureFlagOverride { Id = name.Trim() };
        var applied = entity.Apply(mode, reason, percentage, users, groups);

        return applied.IsFailure
            ? Result.Failure<FeatureFlagOverride>(applied.Errors)
            : Result.Success(entity);
    }

    /// <summary>
    /// Replaces the definition and reason of this override. A cleared override is revived, so setting
    /// a flag again after a clear writes to the same audited row.
    /// </summary>
    /// <param name="mode">What the override sets the flag to.</param>
    /// <param name="reason">Why the override exists.</param>
    /// <param name="percentage">The rollout percentage; required for a percentage override.</param>
    /// <param name="users">The targeted user identifiers.</param>
    /// <param name="groups">The targeted groups.</param>
    /// <returns>Success, or a validation failure that leaves the override unchanged.</returns>
    public Result Change(
        FeatureFlagOverrideMode mode,
        string reason,
        int? percentage = null,
        IEnumerable<string>? users = null,
        IEnumerable<string>? groups = null)
    {
        var applied = Apply(mode, reason, percentage, users, groups);
        if (applied.IsFailure || !IsDeleted)
        {
            return applied;
        }

        return Undelete();
    }

    /// <summary>Clears the override, so configuration is in force for the flag again.</summary>
    /// <returns>Success, or a failure when the override is already cleared.</returns>
    public Result Clear() => Delete();

    private static string[] Split(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(TargetSeparator);

    private static bool CarriesPercentage(FeatureFlagOverrideMode mode) =>
        mode is FeatureFlagOverrideMode.Percentage or FeatureFlagOverrideMode.Targeting;

    /// <summary>
    /// A percentage override must name a percentage; a targeting override may (the share of everyone
    /// outside the named audience); either way it is 0 to 100. Other modes ignore it.
    /// </summary>
    private static bool PercentageFits(FeatureFlagOverrideMode mode, int? percentage) =>
        mode == FeatureFlagOverrideMode.Percentage
            ? percentage is >= 0 and <= MaxPercentage
            : !CarriesPercentage(mode) || percentage is null or >= 0 and <= MaxPercentage;

    /// <summary>The first refusal of the scalar half of a definition, or <see langword="null"/>.</summary>
    private static Error? Refusal(FeatureFlagOverrideMode mode, string reason, int? percentage)
    {
        if (!Enum.IsDefined(mode))
        {
            return Error.Validation(
                "FeatureFlagOverride.ModeInvalid",
                "A feature flag override must be Off, On, Percentage or Targeting.",
                nameof(Change));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > ReasonMaxLength)
        {
            return Error.Validation(
                "FeatureFlagOverride.ReasonRequired",
                "A feature flag override requires a reason of at most 500 characters.",
                nameof(Change));
        }

        return PercentageFits(mode, percentage)
            ? null
            : Error.Validation(
                "FeatureFlagOverride.PercentageInvalid",
                "A percentage override requires a percentage from 0 to 100.",
                nameof(Change));
    }

    /// <summary>The stored audience of a targeting override: at least one user or group.</summary>
    private static Result<(string? Users, string? Groups)> Audience(IEnumerable<string>? users, IEnumerable<string>? groups)
    {
        var normalizedUsers = NormalizeTargets(users);
        if (!normalizedUsers.TryGetValue(out var targetUsers))
        {
            return Result.Failure<(string?, string?)>(normalizedUsers.Errors);
        }

        var normalizedGroups = NormalizeTargets(groups);
        if (!normalizedGroups.TryGetValue(out var targetGroups))
        {
            return Result.Failure<(string?, string?)>(normalizedGroups.Errors);
        }

        if (targetUsers is null && targetGroups is null)
        {
            return Result.Failure<(string?, string?)>(Error.Validation(
                "FeatureFlagOverride.TargetingEmpty",
                "A targeting override must name at least one user or group.",
                nameof(Change)));
        }

        return Result.Success<(string?, string?)>((targetUsers, targetGroups));
    }

    /// <summary>Trims, de-duplicates and joins a target list, or <see langword="null"/> when it names nobody.</summary>
    private static Result<string?> NormalizeTargets(IEnumerable<string>? targets)
    {
        var names = (targets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var joined = string.Join(TargetSeparator, names);
        if (joined.Length > TargetListMaxLength
            || names.Exists(target => target.Length > TargetMaxLength || target.Contains(TargetSeparator, StringComparison.Ordinal)))
        {
            return Result.Failure<string?>(Error.Validation(
                "FeatureFlagOverride.TargetInvalid",
                "Each targeted user or group must be at most 128 characters and contain no comma.",
                nameof(Change)));
        }

        return Result.Success<string?>(names.Count == 0 ? null : joined);
    }

    /// <summary>
    /// Validates a definition and, only when all of it is valid, writes it: a refused change never
    /// leaves the override half-updated.
    /// </summary>
    private Result Apply(
        FeatureFlagOverrideMode mode,
        string reason,
        int? percentage,
        IEnumerable<string>? users,
        IEnumerable<string>? groups)
    {
        if (Refusal(mode, reason, percentage) is { } refusal)
        {
            return Result.Failure(refusal);
        }

        var audience = mode == FeatureFlagOverrideMode.Targeting
            ? Audience(users, groups)
            : Result.Success<(string?, string?)>((null, null));
        if (!audience.TryGetValue(out var targets))
        {
            return Result.Failure(audience.Errors);
        }

        Mode = mode;
        Percentage = CarriesPercentage(mode) ? percentage : null;
        TargetUsers = targets.Item1;
        TargetGroups = targets.Item2;
        Reason = reason.Trim();

        return Result.Success();
    }
}
