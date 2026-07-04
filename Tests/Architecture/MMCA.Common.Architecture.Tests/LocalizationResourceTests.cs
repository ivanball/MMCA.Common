using MMCA.Common.Shared.Globalization;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Asserts the framework's own <c>.resx</c> resources fully translate every supported non-default culture
/// (ADR-027). The required-culture set is derived from <see cref="SupportedCultures.All"/> (today: Spanish,
/// <c>es</c>) so the gate tracks the allowlist instead of duplicating it — adding a locale to
/// <c>SupportedCultures</c> automatically extends the coverage requirement.
/// </summary>
public sealed class LocalizationResourceTests : LocalizationResourceTestsBase
{
    protected override IReadOnlyCollection<string> RequiredCultures =>
        SupportedCultures.All
            .Where(c => !string.Equals(c, SupportedCultures.Default, StringComparison.Ordinal))
            .ToList();

    // Non-vacuous floor: ErrorResources (API), SharedResource + MudTranslations (UI). A wrong scan
    // root or repo re-layout can no longer let the gate pass having checked nothing.
    protected override int MinimumBaseResources => 3;
}
