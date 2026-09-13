namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// Resource anchor type for the role-administration list's strings (ADR-027).
/// <c>RoleAdminList</c> injects <c>IStringLocalizer&lt;RoleAdminListResources&gt;</c> and this marker
/// is what the <c>RoleAdminListResources.resx</c> / <c>RoleAdminListResources.es.resx</c> pair binds
/// to.
/// </summary>
/// <remarks>
/// An app that wants different wording does NOT edit these resources: it passes its own
/// <c>IStringLocalizer</c> as the component's <c>Localizer</c> parameter, and any key that localizer
/// carries wins over the key of the same name here.
/// </remarks>
public sealed class RoleAdminListResources;
