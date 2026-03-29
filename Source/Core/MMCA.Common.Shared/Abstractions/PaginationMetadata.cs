using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;

namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Carries server-side pagination state for paged API responses.
/// Computed properties (<see cref="TotalPageCount"/>, <see cref="FirstRowOnPage"/>, <see cref="LastRowOnPage"/>)
/// are derived from the core values and excluded from serialization.
/// </summary>
[DataContract]
public sealed record PaginationMetadata
{
    /// <summary>Initializes a default (empty) pagination metadata instance.</summary>
    public PaginationMetadata()
        : this(totalItemCount: 0, pageSize: 0, currentPage: 0) { }

    /// <summary>Initializes pagination metadata with the specified values.</summary>
    /// <param name="totalItemCount">Total number of items across all pages.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="currentPage">The 1-based current page number.</param>
    public PaginationMetadata(int totalItemCount, int pageSize, int currentPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(currentPage);

        TotalItemCount = totalItemCount;
        PageSize = pageSize;
        CurrentPage = currentPage;
    }

    /// <summary>Gets the total number of items across all pages.</summary>
    [DataMember(Order = 1)]
    public int TotalItemCount { get; init; }

    /// <summary>Gets the number of items per page.</summary>
    [DataMember(Order = 2)]
    public int PageSize { get; init; }

    /// <summary>Gets the 1-based current page number.</summary>
    [DataMember(Order = 3)]
    public int CurrentPage { get; init; }

    /// <summary>Gets the total number of pages (ceiling division of total items by page size).</summary>
    [IgnoreDataMember]
    public int TotalPageCount => PageSize > 0 ? (int)Math.Ceiling(TotalItemCount / (double)PageSize) : 0;

    /// <summary>Gets the 1-based index of the first item on the current page.</summary>
    [IgnoreDataMember]
    public int FirstRowOnPage => TotalItemCount == 0 || PageSize == 0 ? 0 : (int)((long)(CurrentPage - 1) * PageSize + 1);

    /// <summary>Gets the 1-based index of the last item on the current page, clamped to <see cref="TotalItemCount"/>.</summary>
    [IgnoreDataMember]
    public int LastRowOnPage => PageSize == 0 ? 0 : (int)Math.Min((long)CurrentPage * PageSize, TotalItemCount);
}

/// <summary>
/// Wraps a collection of items for API responses. Serves as the base type for
/// <see cref="PagedCollectionResult{T}"/> when pagination metadata is needed.
/// </summary>
/// <typeparam name="T">The type of items in the collection.</typeparam>
[DataContract]
public record CollectionResult<T>
{
    /// <summary>Initializes an empty collection result.</summary>
    [SetsRequiredMembers]
    public CollectionResult()
        : this(items: []) { }

    /// <summary>Initializes a collection result with the specified items.</summary>
    /// <param name="items">The items to include in the result.</param>
    [SetsRequiredMembers]
    public CollectionResult(IReadOnlyCollection<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items is List<T> list ? list : [.. items];
    }

    /// <summary>Gets or initializes the collection of items.</summary>
    [DataMember(Order = 1)]
    public required ICollection<T> Items { get; init; }
}

/// <summary>
/// A <see cref="CollectionResult{T}"/> augmented with <see cref="PaginationMetadata"/>
/// for paginated API endpoints.
/// </summary>
/// <typeparam name="T">The type of items in the collection.</typeparam>
[DataContract]
public sealed record PagedCollectionResult<T> : CollectionResult<T>
{
    /// <summary>Initializes an empty paged collection result with default pagination metadata.</summary>
    [SetsRequiredMembers]
    public PagedCollectionResult()
        : this(items: [], paginationMetadata: new PaginationMetadata()) { }

    /// <summary>Initializes a paged collection result with the specified items and pagination metadata.</summary>
    /// <param name="items">The items for the current page.</param>
    /// <param name="paginationMetadata">The pagination state.</param>
    [SetsRequiredMembers]
    public PagedCollectionResult(IReadOnlyCollection<T> items, PaginationMetadata paginationMetadata)
        : base(items)
    {
        ArgumentNullException.ThrowIfNull(paginationMetadata);
        PaginationMetadata = paginationMetadata;
    }

    /// <summary>Gets or initializes the pagination metadata.</summary>
    [DataMember(Order = 2)]
    public required PaginationMetadata PaginationMetadata { get; init; }
}
