namespace MMCA.Common.Shared.Legal;

/// <summary>
/// The signed-in user's standing against the host's current Terms of Service: which version is
/// current, which version (if any) the user accepted and when, and whether the two match.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CurrentVersion"/> is <see langword="null"/> when the host configures no terms version.
/// That is the "feature off" answer, and it always comes with <see cref="IsCurrent"/> set to
/// <see langword="true"/>, so a client never blocks a user on a host that has not opted in.
/// </para>
/// <para>
/// Build instances through <see cref="Evaluate"/> so <see cref="IsCurrent"/> is derived the same way
/// on every path (ordinal comparison of the accepted version against the current one).
/// </para>
/// </remarks>
public sealed record LegalAcceptanceDTO
{
    /// <summary>Gets the terms version the host currently requires, or <see langword="null"/> when none is configured.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Gets the terms version the user last accepted, or <see langword="null"/> when they never accepted one.</summary>
    public string? AcceptedVersion { get; init; }

    /// <summary>Gets the UTC instant the user last accepted the terms, or <see langword="null"/>.</summary>
    public DateTime? AcceptedOn { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user is up to date: no version is configured, or the
    /// accepted version equals the current one.
    /// </summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// Builds the standing for one user, deriving <see cref="IsCurrent"/> from the two versions.
    /// </summary>
    /// <param name="currentVersion">The host's configured current version; null or whitespace means none.</param>
    /// <param name="acceptedVersion">The version the user last accepted, if any.</param>
    /// <param name="acceptedOn">When the user last accepted, if ever.</param>
    /// <returns>The evaluated standing.</returns>
    public static LegalAcceptanceDTO Evaluate(string? currentVersion, string? acceptedVersion, DateTime? acceptedOn)
    {
        var current = string.IsNullOrWhiteSpace(currentVersion) ? null : currentVersion.Trim();

        return new LegalAcceptanceDTO
        {
            CurrentVersion = current,
            AcceptedVersion = acceptedVersion,
            AcceptedOn = acceptedOn,
            IsCurrent = current is null || string.Equals(acceptedVersion, current, StringComparison.Ordinal),
        };
    }
}
