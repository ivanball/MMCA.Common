using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Services.Api;

/// <summary>
/// Reads every row of a <c>/paged</c> endpoint by paging until the server's reported total is
/// reached, for hand-written services that need the whole set in one list.
/// </summary>
/// <remarks>
/// A single read truncates silently: the API clamps any requested page size to its own maximum
/// (<c>ApplicationSettings.MaxPageSize</c>, 500 by default), and the GET-all endpoint ignores
/// <c>pageSize</c> entirely and always returns that maximum, so rows past it simply vanish with
/// nothing to say so. Paging until <see cref="PaginationMetadata.TotalItemCount"/> is reached is the
/// only unbounded route.
/// </remarks>
public static class PagedReadAll
{
    /// <summary>Rows per request: the API's default maximum page size, so no request is clamped.</summary>
    public const int PageSize = 500;

    /// <summary>
    /// Runaway guard: 20,000 rows, so a server that keeps reporting a larger total than it returns
    /// stops the loop instead of spinning forever.
    /// </summary>
    private const int MaxPages = 40;

    /// <summary>
    /// Builds the URL for one page of an entity controller's <c>/paged</c> endpoint, in stable id
    /// order so consecutive pages neither skip nor repeat a row, with foreign keys and children
    /// excluded. A host that warms its output cache by replaying these URLs must replay them byte for
    /// byte, so treat the format as part of that contract.
    /// </summary>
    /// <param name="entity">The controller route, for example <c>speakers</c>.</param>
    /// <param name="pageNumber">The 1-based page.</param>
    /// <returns>The relative URL.</returns>
    [SuppressMessage(
        "Design",
        "CA1055:URI-return values should not be strings",
        Justification = "A relative path-and-query that callers both compose as text (an output-cache warm-up replays it byte for byte, and helpers that take a string path) and wrap as a relative Uri for HttpClient; a System.Uri return would force a UriKind choice on the text callers.")]
    public static string LookupPageUrl(string entity, int pageNumber) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{entity}/paged?pageNumber={pageNumber}&pageSize={PageSize}&sortColumn=Id&sortDirection=asc&includeFKs=false&includeChildren=false");

    /// <summary>
    /// Fetches pages 1, 2, ... until a page is empty or the accumulated count reaches the total the
    /// server reports, stopping after 40 pages whatever the server claims.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="fetchPage">Reads one 1-based page.</param>
    /// <param name="cancellationToken">Cancellation token, checked before every page.</param>
    /// <returns>Every row, or the first failure that stopped the read, unchanged.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<Result<List<T>>> ReadAllAsync<T>(
        Func<int, Task<Result<PagedCollectionResult<T>>>> fetchPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);

        List<T> accumulated = [];

        for (int pageNumber = 1; pageNumber <= MaxPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await fetchPage(pageNumber);
            if (!result.TryGetValue(out var page))
            {
                return Result.Failure<List<T>>(result.Errors);
            }

            var items = page.Items ?? [];
            if (items.Count == 0)
            {
                break;
            }

            accumulated.AddRange(items);

            if (accumulated.Count >= page.PaginationMetadata.TotalItemCount)
            {
                break;
            }
        }

        return Result.Success(accumulated);
    }
}
