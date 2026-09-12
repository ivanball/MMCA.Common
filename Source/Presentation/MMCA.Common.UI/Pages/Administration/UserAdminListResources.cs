namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// Resource anchor type for the user-administration list's strings (ADR-027). A generic component
/// cannot own a <c>.resx</c> by its own type name, so <c>UserAdminList&lt;TUser&gt;</c> injects
/// <c>IStringLocalizer&lt;UserAdminListResources&gt;</c> and this non-generic marker is what the
/// <c>UserAdminListResources.resx</c> / <c>UserAdminListResources.es.resx</c> pair binds to.
/// </summary>
/// <remarks>
/// An app that wants different wording does NOT edit these resources: it passes its own
/// <c>IStringLocalizer</c> as the component's <c>Localizer</c> parameter, and any key that localizer
/// carries wins over the key of the same name here.
/// </remarks>
public sealed class UserAdminListResources;
