namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// Resource anchor type for the machine-client administration component's strings (ADR-027).
/// <c>MachineClientAdmin</c> injects <c>IStringLocalizer&lt;MachineClientAdminResources&gt;</c> and this
/// marker is what the <c>MachineClientAdminResources.resx</c> / <c>MachineClientAdminResources.es.resx</c>
/// pair binds to.
/// </summary>
/// <remarks>
/// An app that wants different wording passes its own <c>IStringLocalizer</c> as the component's
/// <c>Localizer</c> parameter; any key that localizer carries wins over the key here.
/// </remarks>
public sealed class MachineClientAdminResources;
