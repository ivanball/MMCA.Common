namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>Fits free text to a bounded database column.</summary>
internal static class ColumnWidth
{
    /// <summary>Truncates a value to a column width, preserving null.</summary>
    /// <param name="value">The value to fit, or null.</param>
    /// <param name="maxLength">The column width.</param>
    /// <returns>The value, cut to at most <paramref name="maxLength"/> characters, or null.</returns>
    internal static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
