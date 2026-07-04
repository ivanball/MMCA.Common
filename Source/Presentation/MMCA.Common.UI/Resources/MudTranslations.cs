namespace MMCA.Common.UI.Resources;

/// <summary>
/// Resource anchor type for MudBlazor's built-in component text (data-grid pager and filter menus,
/// pickers, table editing, pagination, snackbar/alert close buttons, input adornments) — ADR-027.
/// Resolved through <see cref="Globalization.ResxMudLocalizer"/> so MudBlazor chrome follows the
/// active UI culture; its <c>.resx</c> siblings mirror MudBlazor's own <c>LanguageResource</c> keys
/// (v9.6.0) with the English values copied verbatim, so en-US behavior is unchanged.
/// </summary>
public sealed class MudTranslations;
