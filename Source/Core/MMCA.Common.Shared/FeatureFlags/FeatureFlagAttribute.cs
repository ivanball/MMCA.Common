using System.Globalization;

namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// Declares the lifecycle of one feature flag constant, so a toggle that has outlived its purpose
/// is a failing build rather than an archaeology exercise (ADR-031).
/// <para>
/// Applied to the <c>public const string</c> fields of a <c>*Features</c> class. A
/// <see cref="FeatureFlagLifetime.Temporary"/> flag must name the date it is expected to be gone by
/// (<see cref="RemoveBy"/>); a <see cref="FeatureFlagLifetime.Permanent"/> one must not, because a
/// removal date on a flag nobody intends to remove is exactly the noise that teaches people to
/// ignore the gate. The <c>FeatureFlagLifecycleTestsBase</c> fitness pair in
/// <c>MMCA.Common.Testing.Architecture</c> enforces both halves and fails the build the day a
/// temporary flag passes its date.
/// </para>
/// </summary>
/// <param name="lifetime">Whether the flag is permanent or temporary.</param>
/// <example>
/// <code>
/// public static class CatalogFeatures
/// {
///     [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "Catalog")]
///     public const string Recommendations = "Catalog.Recommendations";
///
///     [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "2026-12-31", Owner = "Catalog")]
///     public const string NewPricingEngine = "Catalog.NewPricingEngine";
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class FeatureFlagAttribute(FeatureFlagLifetime lifetime) : Attribute
{
    /// <summary>The date format <see cref="RemoveBy"/> is written in: ISO 8601 <c>yyyy-MM-dd</c>.</summary>
    public const string RemoveByFormat = "yyyy-MM-dd";

    /// <summary>Gets whether the flag is permanent or temporary.</summary>
    public FeatureFlagLifetime Lifetime { get; } = lifetime;

    /// <summary>
    /// Gets the date the flag is expected to be gone by, as ISO <c>yyyy-MM-dd</c>. Required on a
    /// <see cref="FeatureFlagLifetime.Temporary"/> flag and forbidden on a
    /// <see cref="FeatureFlagLifetime.Permanent"/> one. A string rather than a
    /// <see cref="DateOnly"/> because an attribute argument must be a compile-time constant.
    /// </summary>
    public string? RemoveBy { get; init; }

    /// <summary>
    /// Gets the team or module that owns the decision to remove the flag, e.g. <c>"MMCA.Common"</c>
    /// or <c>"Catalog"</c>. Carried into the failure message, so an expired flag names someone.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    /// Parses <see cref="RemoveBy"/> into a date.
    /// </summary>
    /// <param name="removeBy">The raw attribute value.</param>
    /// <param name="date">The parsed date when the value is a valid ISO <c>yyyy-MM-dd</c> string.</param>
    /// <returns><see langword="true"/> when the value parsed.</returns>
    public static bool TryParseRemoveBy(string? removeBy, out DateOnly date) =>
        DateOnly.TryParseExact(
            removeBy,
            RemoveByFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
}
