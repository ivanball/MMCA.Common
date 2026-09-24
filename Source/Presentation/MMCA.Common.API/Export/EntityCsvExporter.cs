using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MMCA.Common.Application.Services;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Export;

/// <summary>
/// The CSV machinery behind <c>EntityControllerBase.ExportAsync</c>: which DTO properties can be
/// exported, how a page of rows becomes CSV records, and the paging loop that streams every page to
/// the response body under a row ceiling. The controller keeps the HTTP concerns (the action, the
/// row scope, the file name and headers); this type owns the format.
/// </summary>
/// <typeparam name="TEntityDTO">The DTO the export is shaped from.</typeparam>
internal static class EntityCsvExporter<TEntityDTO>
{
    /// <summary>
    /// The DTO property names an export omits, because their values cannot render a faithful scalar
    /// CSV cell: binary concurrency tokens (<see langword="byte"/>[], <see cref="ReadOnlyMemory{T}"/>
    /// of bytes) and every collection-typed property except <see langword="string"/>. Computed once
    /// per closed DTO type, since a DTO's shape cannot change at runtime.
    /// </summary>
    /// <remarks>
    /// Value objects and other class-typed properties are deliberately NOT dropped: a record or
    /// value object has a meaningful invariant <c>ToString</c>, which is exactly the cell a reader
    /// expects. Only the two categories above render a type name instead of a value.
    /// </remarks>
    private static readonly string[] UnexportablePropertyNames =
    [
        .. typeof(TEntityDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && !IsExportableType(p.PropertyType))
            .Select(p => p.Name)
    ];

    /// <summary>
    /// <see cref="UnexportablePropertyNames"/> as the camelCase column names a shaped row carries,
    /// for filtering a resolved column list.
    /// </summary>
    private static readonly HashSet<string> UnexportableColumns =
        [.. EntityCsvExporter<TEntityDTO>.UnexportablePropertyNames.Select(JsonNamingPolicy.CamelCase.ConvertName)];

    /// <summary>
    /// <see cref="UnexportablePropertyNames"/> as a case-insensitive set, for matching the names a
    /// caller spells in <c>fields=</c>.
    /// </summary>
    private static readonly HashSet<string> UnexportableFields =
        new(EntityCsvExporter<TEntityDTO>.UnexportablePropertyNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects a <c>fields=</c> request that names a property the export cannot render. Answering
    /// such a request with the column quietly missing would hide the contract from the caller, so it
    /// fails the same way an unknown field does on the JSON endpoints: an
    /// <c>Error.InvalidEntityField</c> validation failure, before a single byte of body is written.
    /// </summary>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The validation errors, or <see langword="null"/> when the request is clean.</returns>
    internal static List<Error>? ValidateFields(string? fields)
    {
        if (UnexportableFields.Count == 0)
            return null;

        List<Error> errors =
        [
            .. ParseFields(fields)
                .Where(UnexportableFields.Contains)
                .Select(field => Error.InvalidEntityField with
                {
                    Message = $"Field '{field}' on type '{typeof(TEntityDTO).Name}' cannot be exported to CSV: binary and collection properties have no faithful CSV representation.",
                    Target = typeof(TEntityDTO).Name
                })
        ];

        return errors.Count > 0 ? errors : null;
    }

    /// <summary>
    /// Streams every page <paramref name="fetchPageAsync"/> returns to <paramref name="body"/> as
    /// CSV, stopping at <paramref name="maxExportRows"/> data rows.
    /// </summary>
    /// <param name="body">The response body. Left open.</param>
    /// <param name="fetchPageAsync">Fetches one 1-based page of <paramref name="pageSize"/> rows.</param>
    /// <param name="pageSize">The page size to fetch with.</param>
    /// <param name="maxExportRows">The data-row ceiling.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <param name="beginResponse">
    /// Sets the response headers. Called once, after the first page succeeded and before the first
    /// body byte is written.
    /// </param>
    /// <param name="onFailureAfterStart">
    /// Reports a page failure that arrived after the body had started (the errors and the data rows
    /// already written). The file then ends with an "incomplete" marker line.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success once the body is written; the first page's failure, with nothing written, when that
    /// page failed (the caller can still answer with Problem Details).
    /// </returns>
    /// <remarks>
    /// Constructing the writer sends nothing: the response stays uncommitted until the first flush,
    /// which is what keeps the "failure on page one still returns Problem Details" path honest.
    /// </remarks>
    internal static async Task<Result> WriteAsync(
        Stream body,
        Func<int, CancellationToken, Task<Result<PagedCollectionResult<object>>>> fetchPageAsync,
        int pageSize,
        int maxExportRows,
        string? fields,
        Action beginResponse,
        Action<IReadOnlyList<Error>, int> onFailureAfterStart,
        CancellationToken cancellationToken)
    {
        var pageNumber = 1;
        var rowsWritten = 0;
        var truncated = false;
        var started = false;
        IReadOnlyList<string> columns = [];

        var writer = new StreamWriter(body, CsvWriter.Utf8NoPreamble, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            while (true)
            {
                var result = await fetchPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    if (!started)
                        return Result.Failure(result.Errors);

                    // The status line left the building with the header row. A trailing marker is the
                    // only signal still available, so the file says it is short rather than looking
                    // like a complete export of fewer rows.
                    onFailureAfterStart(result.Errors, rowsWritten);
                    CsvWriter.WriteRow([IncompleteMarker(rowsWritten)], writer);
                    break;
                }

                var page = result.Value!;
                var items = page.Items;

                if (!started)
                {
                    columns = ResolveColumns(items, fields);
                    beginResponse();
                    CsvWriter.WriteByteOrderMark(writer);
                    CsvWriter.WriteHeader(columns, writer);
                    started = true;
                }

                var pageRows = WritePage(items, columns, fields, maxExportRows - rowsWritten, writer);
                rowsWritten += pageRows;

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                // Stopped mid-page: rows were definitely left behind.
                if (pageRows < items.Count)
                {
                    truncated = true;
                    break;
                }

                // A short page is the last page, cap or no cap.
                if (items.Count < pageSize)
                    break;

                // The cap landed exactly on a page boundary: only the total says whether anything
                // remains, and asking for one more page just to find out would be a wasted query.
                if (rowsWritten >= maxExportRows)
                {
                    truncated = page.PaginationMetadata.TotalItemCount > rowsWritten;
                    break;
                }

                pageNumber++;
            }

            if (truncated)
            {
                CsvWriter.WriteRow([TruncationMarker(rowsWritten)], writer);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Decides whether a DTO property type can render a faithful scalar CSV cell. See
    /// <see cref="UnexportablePropertyNames"/> for what the two exclusions buy.
    /// </summary>
    /// <param name="type">The declared property type.</param>
    /// <returns><see langword="true"/> when the property earns a column.</returns>
    private static bool IsExportableType(Type type) =>
        type != typeof(byte[])
        && type != typeof(ReadOnlyMemory<byte>)
        && (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type));

    /// <summary>Whether a resolved column survives the exclusions in <see cref="UnexportableColumns"/>.</summary>
    /// <param name="column">The camelCase column name.</param>
    /// <returns><see langword="true"/> when the column is written.</returns>
    private static bool IsExportableColumn(string column) => !UnexportableColumns.Contains(column);

    /// <summary>
    /// Writes one page of results as CSV records, stopping early once <paramref name="remainingRows"/>
    /// have been written (the row ceiling landing inside this page).
    /// </summary>
    /// <param name="items">The page of rows.</param>
    /// <param name="columns">The column names, in output order.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <param name="remainingRows">How many more rows the export may still write.</param>
    /// <param name="writer">The destination writer.</param>
    /// <returns>The number of rows written, which is short of the page when the ceiling was reached.</returns>
    private static int WritePage(
        ICollection<object> items,
        IReadOnlyList<string> columns,
        string? fields,
        int remainingRows,
        TextWriter writer)
    {
        var written = 0;

        foreach (var item in items)
        {
            if (written >= remainingRows)
                break;

            CsvWriter.WriteRow(BuildCells(ShapeRow(item, fields), columns), writer);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Resolves the CSV column names for an export from the first page of results, falling back to the
    /// DTO's own shape when that page is empty (an export of nothing still owes the caller a header
    /// row naming the columns).
    /// </summary>
    /// <remarks>
    /// Both paths drop the columns named in <see cref="UnexportableColumns"/>: the shaped-row path
    /// filters the row's own keys, the empty-page path filters the DTO's properties by type. A
    /// dropped property yields no column at all rather than an empty one, so a reader never sees a
    /// header it cannot trust.
    /// </remarks>
    /// <param name="items">The first page of rows, typed DTOs or already-shaped dynamic objects.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The column names, in output order, as camelCase JSON names.</returns>
    private static IReadOnlyList<string> ResolveColumns(ICollection<object> items, string? fields)
    {
        var first = items.FirstOrDefault();
        if (first is not null)
            return [.. ShapeRow(first, fields).Keys.Where(IsExportableColumn)];

        var requested = ParseFields(fields);

        // Property declaration order, filtered by the requested fields: exactly the order
        // QueryFieldService.ShapeCollectionData would have produced had there been a row to shape.
        return
        [
            .. typeof(TEntityDTO)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                    && IsExportableType(p.PropertyType)
                    && (requested.Count == 0 || requested.Contains(p.Name)))
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
        ];
    }

    /// <summary>
    /// Normalizes one result row to a camelCase-keyed dictionary. The query service already returns
    /// shaped dynamic objects when a field subset was requested, and typed DTOs otherwise, so this
    /// shapes only the second case and reuses <see cref="QueryFieldService"/>'s compiled accessors to
    /// do it. Either way the keys are the JSON property names.
    /// </summary>
    /// <param name="item">The row to normalize.</param>
    /// <param name="fields">The requested field projection, or null for all fields.</param>
    /// <returns>The row as a name-to-value dictionary.</returns>
    private static IDictionary<string, object?> ShapeRow(object item, string? fields) =>
        item as IDictionary<string, object?>
        ?? QueryFieldService.ShapeData((TEntityDTO)item, fields);

    /// <summary>
    /// Projects a shaped row onto the resolved column order, substituting null for a column the row
    /// does not carry.
    /// </summary>
    /// <param name="row">The shaped row.</param>
    /// <param name="columns">The column names, in output order.</param>
    /// <returns>The cell values for one CSV record.</returns>
    private static object?[] BuildCells(IDictionary<string, object?> row, IReadOnlyList<string> columns)
    {
        var cells = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            cells[i] = row.TryGetValue(columns[i], out var value) ? value : null;
        }

        return cells;
    }

    /// <summary>Splits a comma-separated <c>fields=</c> value into a case-insensitive set.</summary>
    /// <param name="fields">The requested field projection, or null.</param>
    /// <returns>The requested field names; empty when none were requested.</returns>
    private static HashSet<string> ParseFields(string? fields) =>
        fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    /// <summary>The final line appended to an export stopped by the row ceiling.</summary>
    /// <param name="rowsWritten">How many data rows were written.</param>
    /// <returns>The marker text, written as a single-cell record.</returns>
    private static string TruncationMarker(int rowsWritten) =>
        string.Create(CultureInfo.InvariantCulture, $"# export truncated at {rowsWritten} rows");

    /// <summary>The final line appended to an export abandoned by a query failure mid-stream.</summary>
    /// <param name="rowsWritten">How many data rows were written before the failure.</param>
    /// <returns>The marker text, written as a single-cell record.</returns>
    private static string IncompleteMarker(int rowsWritten) =>
        string.Create(CultureInfo.InvariantCulture, $"# export incomplete after {rowsWritten} rows");
}
