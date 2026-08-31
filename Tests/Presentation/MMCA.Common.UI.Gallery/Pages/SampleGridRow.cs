using System.Globalization;

namespace MMCA.Common.UI.Gallery.Pages;

/// <summary>
/// The row DTO behind the virtualized-grid gallery page. Deliberately plain: the point of
/// <c>/grid</c> is the windowing behaviour of <c>DataGridListPageBase</c>, not the shape of the data.
/// </summary>
public sealed record SampleGridRow(int Id, string Name, string Category, DateTime CreatedOn, decimal Amount);

/// <summary>
/// The gallery's in-memory stand-in for a paged API. The rows are generated once from fixed inputs
/// (no randomness, no clock), so every E2E run, on every engine, scrolls over an identical data set
/// and a row assertion can name an exact value.
/// </summary>
internal static class SampleGridData
{
    /// <summary>The row count the E2E windowing assertion is written against.</summary>
    public const int RowCount = 1000;

    private static readonly string[] Categories = ["Hardware", "Software", "Services", "Training", "Support"];

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<SampleGridRow> All { get; } = Build();

    private static SampleGridRow[] Build()
    {
        var rows = new SampleGridRow[RowCount];
        for (var i = 0; i < rows.Length; i++)
        {
            var id = i + 1;
            rows[i] = new SampleGridRow(
                id,
                string.Create(CultureInfo.InvariantCulture, $"Row {id:D4}"),
                Categories[i % Categories.Length],
                Epoch.AddHours(i),
                id * 37 % 10000 / 100m);
        }

        return rows;
    }
}
