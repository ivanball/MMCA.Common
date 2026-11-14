using System.Globalization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Gallery.Pages;

/// <summary>A row of the gallery's paged <c>/grid/paged</c> list.</summary>
/// <param name="Id">The row id.</param>
/// <param name="Email">An email, numbered so a page's rows are recognizable.</param>
public sealed record GalleryUser(int Id, string Email)
{
    /// <summary>The number of rows the list pages over.</summary>
    public const int Count = 60;

    /// <summary>One page of the canned rows, in id order (the sort is ignored on purpose).</summary>
    /// <param name="pageNumber">The one-based page number.</param>
    /// <param name="pageSize">The page size.</param>
    /// <returns>The page and the total count.</returns>
    public static Result<(IReadOnlyList<GalleryUser> Items, int TotalItems)> Page(int pageNumber, int pageSize)
    {
        IReadOnlyList<GalleryUser> items = [.. Enumerable.Range((pageNumber - 1) * pageSize + 1, pageSize)
            .Where(static i => i <= Count)
            .Select(static i => new GalleryUser(i, string.Create(CultureInfo.InvariantCulture, $"user{i:00}@example.com")))];
        return Result.Success((items, Count));
    }
}
