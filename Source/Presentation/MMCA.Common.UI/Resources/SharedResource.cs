namespace MMCA.Common.UI.Resources;

/// <summary>
/// Resource anchor type for cross-cutting UI chrome strings shared across components and pages
/// (buttons, layout labels, snackbar/error templates, culture and theme switcher text) — ADR-027.
/// Injected as <c>IStringLocalizer&lt;SharedResource&gt;</c>; its <c>.resx</c> siblings
/// (<c>SharedResource.resx</c> / <c>SharedResource.es.resx</c>) hold the dotted, stable keys.
/// </summary>
public sealed class SharedResource
{
}
