namespace MMCA.Common.UI.Services;

/// <summary>
/// One grid column filter as a list page saves it: the MudDataGrid operator (for example
/// <c>contains</c> or <c>&gt;=</c>) and the filter value written in the invariant culture, or
/// <see langword="null"/> for an operator that takes no value (<c>is empty</c>).
/// </summary>
/// <param name="Operator">The MudDataGrid filter operator.</param>
/// <param name="Value">The invariant-culture filter value, or <see langword="null"/> when the operator takes none.</param>
public sealed record ColumnFilterState(string Operator, string? Value);
