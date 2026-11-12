using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Imports;

/// <summary>
/// One row-level problem of a file import: where it is (file line, and the column when one is to
/// blame) and what it is (error code and message).
/// </summary>
public sealed record ImportRowErrorDTO
{
    /// <summary>
    /// Gets the 1-based file line the record starts on (the header is line 1), so a quoted line break
    /// in an earlier record counts; this is <c>CsvRow.LineNumber</c>.
    /// </summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the column the error is about, or <see langword="null"/> for a whole-row error.</summary>
    public string? Column { get; init; }

    /// <summary>Gets the error code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the error message.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Builds a row error from a domain <see cref="Error"/>, copying its code and message.
    /// </summary>
    /// <param name="error">The error found on the row.</param>
    /// <param name="lineNumber">The 1-based file line the record starts on.</param>
    /// <param name="column">The column the error is about, or <see langword="null"/> for a whole-row error.</param>
    /// <returns>The row error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static ImportRowErrorDTO FromError(Error error, int lineNumber, string? column = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new ImportRowErrorDTO
        {
            LineNumber = lineNumber,
            Column = column,
            Code = error.Code,
            Message = error.Message,
        };
    }
}
