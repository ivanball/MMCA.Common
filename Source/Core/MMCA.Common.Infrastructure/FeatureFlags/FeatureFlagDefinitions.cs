using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.FeatureFlags;

/// <summary>
/// Translates between a stored override and the <see cref="FeatureDefinition"/> Microsoft.FeatureManagement
/// evaluates, and summarizes any definition into the coarse state the administration surface reports.
/// </summary>
/// <remarks>
/// An override is expressed with the SAME built-in filters configuration uses, so the feature manager
/// evaluates it exactly as it would the equivalent configuration: <c>AlwaysOn</c> for on, no filter for
/// off, and the targeting filter for a percentage or a targeted audience. A percentage goes through the
/// targeting filter's default rollout rather than the percentage filter so one user keeps the same
/// answer across requests (the reason <c>AddAPI</c> wires <c>WithTargeting</c>).
/// </remarks>
internal static class FeatureFlagDefinitions
{
    /// <summary>The built-in filter name a configured <see langword="true"/> becomes.</summary>
    internal const string AlwaysOnFilter = "AlwaysOn";

    /// <summary>The built-in targeting filter.</summary>
    internal const string TargetingFilter = "Microsoft.Targeting";

    /// <summary>The full rollout a targeted group receives.</summary>
    private const string WholeGroup = "100";

    /// <summary>Builds the definition the feature manager evaluates for a stored override.</summary>
    /// <param name="stored">The stored override.</param>
    /// <returns>The equivalent feature definition.</returns>
    public static FeatureDefinition ToDefinition(FeatureFlagOverrideResponse stored) => stored.Mode switch
    {
        FeatureFlagOverrideMode.On => Definition(stored.Name, new FeatureFilterConfiguration
        {
            Name = AlwaysOnFilter,
            Parameters = new ConfigurationBuilder().Build(),
        }),
        FeatureFlagOverrideMode.Percentage => Definition(stored.Name, Targeting([], [], stored.Percentage ?? 0)),
        FeatureFlagOverrideMode.Targeting => Definition(stored.Name, Targeting(stored.Users, stored.Groups, stored.Percentage ?? 0)),
        FeatureFlagOverrideMode.Off => Definition(stored.Name),

        // A mode this build does not know evaluates to off: no filter can enable it.
        _ => Definition(stored.Name),
    };

    /// <summary>Summarizes a definition into the state the administration surface reports.</summary>
    /// <param name="definition">The definition, or <see langword="null"/> for a flag nothing defines.</param>
    /// <returns>The coarse state, and the filters that decide a conditional one.</returns>
    public static (FeatureFlagValueKind Kind, IReadOnlyList<string> Filters) Describe(FeatureDefinition? definition)
    {
        if (definition is null || definition.Status == FeatureStatus.Disabled)
        {
            return (FeatureFlagValueKind.Off, []);
        }

        var filters = (definition.EnabledFor ?? [])
            .Select(filter => filter.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (filters.Count == 0)
        {
            return (FeatureFlagValueKind.Off, []);
        }

        var anyAlwaysOn = filters.Exists(IsAlwaysOn);
        if (filters.TrueForAll(IsAlwaysOn) || anyAlwaysOn && definition.RequirementType == RequirementType.Any)
        {
            return (FeatureFlagValueKind.On, []);
        }

        return (FeatureFlagValueKind.Conditional, filters);
    }

    private static bool IsAlwaysOn(string name) =>
        string.Equals(name, AlwaysOnFilter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "On", StringComparison.OrdinalIgnoreCase);

    private static FeatureDefinition Definition(string name, params FeatureFilterConfiguration[] enabledFor) =>
        new() { Name = name, EnabledFor = enabledFor };

    /// <summary>
    /// The targeting filter's parameters, built as the configuration section the filter binds from
    /// (<c>Audience:Users</c>, <c>Audience:Groups</c>, <c>Audience:DefaultRolloutPercentage</c>), so the
    /// binding is the filter's own rather than a copy of its settings shape.
    /// </summary>
    private static FeatureFilterConfiguration Targeting(
        IReadOnlyList<string> users,
        IReadOnlyList<string> groups,
        int defaultRolloutPercentage)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Audience:DefaultRolloutPercentage"] = defaultRolloutPercentage.ToString(CultureInfo.InvariantCulture),
        };

        for (var i = 0; i < users.Count; i++)
        {
            values[string.Create(CultureInfo.InvariantCulture, $"Audience:Users:{i}")] = users[i];
        }

        for (var i = 0; i < groups.Count; i++)
        {
            values[string.Create(CultureInfo.InvariantCulture, $"Audience:Groups:{i}:Name")] = groups[i];
            values[string.Create(CultureInfo.InvariantCulture, $"Audience:Groups:{i}:RolloutPercentage")] = WholeGroup;
        }

        return new FeatureFilterConfiguration
        {
            Name = TargetingFilter,
            Parameters = new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
        };
    }
}
