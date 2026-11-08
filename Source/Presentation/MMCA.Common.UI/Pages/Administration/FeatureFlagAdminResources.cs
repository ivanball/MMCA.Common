namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// Resource anchor type for the feature-flag administration component's strings (ADR-027).
/// <c>FeatureFlagAdmin</c> injects <c>IStringLocalizer&lt;FeatureFlagAdminResources&gt;</c> and this
/// marker is what the <c>FeatureFlagAdminResources.resx</c> / <c>FeatureFlagAdminResources.es.resx</c>
/// pair binds to.
/// </summary>
/// <remarks>
/// An app that wants different wording passes its own <c>IStringLocalizer</c> as the component's
/// <c>Localizer</c> parameter; any key that localizer carries wins over the key here.
/// </remarks>
public sealed class FeatureFlagAdminResources;
