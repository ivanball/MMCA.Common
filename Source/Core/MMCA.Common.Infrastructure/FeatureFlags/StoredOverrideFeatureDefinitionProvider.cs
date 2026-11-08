using Microsoft.FeatureManagement;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.FeatureFlags;

/// <summary>
/// The <see cref="IFeatureDefinitionProvider"/> <c>AddReloadableFeatureFlags</c> layers over the one the
/// host already registered (the configuration provider <c>AddFeatureManagement</c> adds): a flag with a
/// stored override answers with the override's definition, and every other flag is answered by the
/// configured provider, unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Nothing above it changes. The FeatureGate decorators keep calling <see cref="IFeatureManager"/>, and
/// the feature manager asks this provider for the definition on every evaluation, so a stored override
/// takes effect on the next evaluation after the cached snapshot is invalidated, with no restart.
/// </para>
/// <para>
/// Names compare case-insensitively, like the configuration keys the configured provider reads.
/// </para>
/// </remarks>
/// <param name="configured">The provider this one is layered over.</param>
/// <param name="snapshot">The cached stored overrides.</param>
internal sealed class StoredOverrideFeatureDefinitionProvider(
    IFeatureDefinitionProvider configured,
    IFeatureFlagOverrideSnapshot snapshot) : IFeatureDefinitionProvider
{
    /// <summary>
    /// Gets the provider this one is layered over, which is what "the configured value" of a flag means
    /// on the administration surface.
    /// </summary>
    internal IFeatureDefinitionProvider Configured => configured;

    /// <inheritdoc />
    public async Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName)
    {
        var stored = Find(await snapshot.GetAsync().ConfigureAwait(false), featureName);

        return stored is null
            ? await configured.GetFeatureDefinitionAsync(featureName).ConfigureAwait(false)
            : FeatureFlagDefinitions.ToDefinition(stored);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
    {
        var overrides = await snapshot.GetAsync().ConfigureAwait(false);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var definition in configured.GetAllFeatureDefinitionsAsync().ConfigureAwait(false))
        {
            answered.Add(definition.Name);
            var stored = Find(overrides, definition.Name);
            yield return stored is null ? definition : FeatureFlagDefinitions.ToDefinition(stored);
        }

        // An override of a flag configuration never mentions is still a definition the host has.
        foreach (var stored in overrides.Where(o => !answered.Contains(o.Name)))
        {
            yield return FeatureFlagDefinitions.ToDefinition(stored);
        }
    }

    private static FeatureFlagOverrideResponse? Find(IReadOnlyList<FeatureFlagOverrideResponse> overrides, string? name) =>
        overrides.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
}
