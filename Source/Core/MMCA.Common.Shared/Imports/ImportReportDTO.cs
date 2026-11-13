namespace MMCA.Common.Shared.Imports;

/// <summary>
/// The outcome of a file import, dry run or commit: the generic row counts and the row-level errors.
/// An application adds its own counts on a derived record.
/// </summary>
/// <remarks>
/// Serializes camelCase like every other DTO; <see cref="RowErrors"/> is always an array, never null,
/// so a client iterates it unconditionally. See <see cref="ImportConventions"/> for the dry-run and
/// status-code conventions.
/// </remarks>
public record class ImportReportDTO
{
    /// <summary>Gets a value indicating whether this report describes a dry run that wrote nothing.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Gets the number of data rows in the file (the header excluded).</summary>
    public required int TotalRows { get; init; }

    /// <summary>Gets the number of rows that added a record (or would have, on a dry run).</summary>
    public int Added { get; init; }

    /// <summary>Gets the number of rows that updated a record (or would have, on a dry run).</summary>
    public int Updated { get; init; }

    /// <summary>Gets the number of rows that matched a record already holding the same values.</summary>
    public int Unchanged { get; init; }

    /// <summary>Gets the number of rows refused; each has at least one entry in <see cref="RowErrors"/>.</summary>
    public int Rejected { get; init; }

    /// <summary>Gets the row-level errors, in file order. Empty, never null, when every row was accepted.</summary>
    public IReadOnlyList<ImportRowErrorDTO> RowErrors { get; init; } = [];
}
