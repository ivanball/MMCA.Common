using System.Globalization;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Matched by full name, and the property values read reflectively, because this package
    // deliberately takes no framework reference (see the csproj note). The attribute itself lives in
    // MMCA.Common.Shared.FeatureFlags.
    private const string FeatureFlagAttributeFullName = "MMCA.Common.Shared.FeatureFlags.FeatureFlagAttribute";
    private const string FeatureClassSuffix = "Features";
    private const string RemoveByFormat = "yyyy-MM-dd";
    private const string TemporaryLifetime = "Temporary";
    private const string PermanentLifetime = "Permanent";

    /// <summary>
    /// Every <c>public const string</c> on a static <c>*Features</c> class declares its lifecycle
    /// with <c>[FeatureFlag]</c>: permanent flags carry no removal date, temporary flags carry a
    /// parseable ISO <c>yyyy-MM-dd</c> one (ADR-031).
    /// </summary>
    /// <remarks>
    /// The scan covers every Shared assembly on the map, framework and per-module alike
    /// (<see cref="IArchitectureMap.OfLayer"/> rather than <c>ModuleShared()</c>), because MMCA.Common
    /// declares flags of its own in a module-less map and a rule that skipped them would be vacuous
    /// in the repo that ships the attribute.
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    public static void FeatureFlagsDeclareLifetime(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = FeatureFlagFields(map).Select(Describe).SelectMany(Check);

        ArchitectureAssert.NoViolations(violations,
            "every feature flag constant must declare its lifecycle with [FeatureFlag]: Permanent without a RemoveBy, "
            + $"Temporary with a parseable {RemoveByFormat} RemoveBy (ADR-031)");

        static IEnumerable<string> Check(FlagDeclaration flag)
        {
            if (flag.Lifetime is null)
            {
                yield return $"  - {flag.Name} carries no [FeatureFlag]: declare it Permanent (a capability a host chooses) or Temporary with a RemoveBy date";
                yield break;
            }

            if (string.Equals(flag.Lifetime, PermanentLifetime, StringComparison.Ordinal) && flag.RemoveBy is not null)
            {
                yield return $"  - {flag.Name} is Permanent but sets RemoveBy='{flag.RemoveBy}': a removal date on a flag nobody intends to remove trains reviewers to ignore the gate";
            }

            if (string.Equals(flag.Lifetime, TemporaryLifetime, StringComparison.Ordinal) && !TryParseRemoveBy(flag.RemoveBy, out _))
            {
                yield return flag.RemoveBy is null
                    ? $"  - {flag.Name} is Temporary and must set RemoveBy (ISO {RemoveByFormat}): a temporary flag with no end date is a permanent one"
                    : $"  - {flag.Name} is Temporary and its RemoveBy='{flag.RemoveBy}' is not an ISO {RemoveByFormat} date";
            }
        }
    }

    /// <summary>
    /// No <c>[FeatureFlag(FeatureFlagLifetime.Temporary)]</c> flag is past its <c>RemoveBy</c> date.
    /// This failing build IS the dead-toggle detector: the branch the flag guards has outlived the
    /// rollout it was written for, and the fix is to delete the flag and the losing branch (ADR-031).
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="today">
    /// The date to judge against, injected so the rule is testable and so a repo can freeze it (for
    /// example to a release date) rather than to the agent's clock.
    /// </param>
    public static void TemporaryFeatureFlagsAreNotPastRemoveBy(IArchitectureMap map, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = FeatureFlagFields(map)
            .Select(Describe)
            .Where(f => string.Equals(f.Lifetime, TemporaryLifetime, StringComparison.Ordinal))
            .Where(f => TryParseRemoveBy(f.RemoveBy, out var removeBy) && today > removeBy)
            .Select(f => $"  - {f.Name} (RemoveBy={f.RemoveBy}, owner={f.Owner ?? "unassigned"}) is past its removal date: delete the flag and the branch it no longer chooses between");

        ArchitectureAssert.NoViolations(violations,
            $"temporary feature flags must be removed by their RemoveBy date (today is {today.ToString(RemoveByFormat, CultureInfo.InvariantCulture)}, ADR-031)");
    }

    /// <summary>One feature-flag constant, flattened out of its attribute so the rules read as rules.</summary>
    private sealed record FlagDeclaration(string Name, string? Lifetime, string? RemoveBy, string? Owner);

    private static IEnumerable<FieldInfo> FeatureFlagFields(IArchitectureMap map) =>
        map.OfLayer(Layer.Shared)
            .SelectMany(a => a.LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true }
                && t.SimpleName.EndsWith(FeatureClassSuffix, StringComparison.Ordinal))
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string));

    private static FlagDeclaration Describe(FieldInfo field)
    {
        var attribute = field.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => string.Equals(a.GetType().FullName, FeatureFlagAttributeFullName, StringComparison.Ordinal));

        return new FlagDeclaration(
            $"{field.DeclaringType?.FullName}.{field.Name}",
            ReadValue(attribute, "Lifetime"),
            ReadValue(attribute, "RemoveBy"),
            ReadValue(attribute, "Owner"));
    }

    private static string? ReadValue(object? attribute, string propertyName) =>
        attribute?.GetType().GetProperty(propertyName)?.GetValue(attribute)?.ToString();

    private static bool TryParseRemoveBy(string? removeBy, out DateOnly date) =>
        DateOnly.TryParseExact(removeBy, RemoveByFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
