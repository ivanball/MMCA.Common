namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// MudDataGrid sorting fitness function: a sortable column must be a <c>PropertyColumn</c>. Server-side
/// sort reads the bound property off the column, and a <c>TemplateColumn</c> has none, so
/// <c>Sortable="true"</c> on one renders a header that toggles an arrow without ordering the data. The
/// defect compiles, renders, and is invisible until someone checks the order, which is exactly the
/// class of regression a fitness function should own.
/// <para>
/// This base scans <c>.razor</c> markup text under the roots the subclass supplies, so it works for any
/// repo layout: point <see cref="MarkupRoots"/> at the UI project (or the whole <c>Source</c> tree) and
/// the whole grid surface is covered. <c>@* *@</c> comments are ignored, and a root that does not exist
/// fails the test rather than silently passing.
/// </para>
/// </summary>
public abstract class SortableColumnConventionTestsBase
{
    /// <summary>
    /// Absolute directory paths scanned recursively for <c>*.razor</c> files. Build a path from
    /// <see cref="ArchitectureMapBase.FindRepoRoot"/> so the scan is independent of the runner's
    /// working directory, e.g.
    /// <c>Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.ADC.slnx"), "Source")</c>.
    /// </summary>
    protected abstract IReadOnlyCollection<string> MarkupRoots { get; }

    [Fact]
    public void SortableColumns_ShouldNotBe_TemplateColumns() =>
        ArchitectureRules.SortableGridColumnsUsePropertyColumn(MarkupRoots);
}
