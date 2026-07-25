namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// The one place page number and page size are turned into a Skip/Take pair.
/// </summary>
/// <remarks>
/// <para>
/// The offset is computed in 64-bit and range-checked rather than left to 32-bit arithmetic:
/// <c>(pageNumber - 1) * pageSize</c> overflows an <see cref="int"/> for page numbers near
/// <see cref="int.MaxValue"/> and wraps NEGATIVE, and a negative <c>Skip</c> is not a benign
/// no-op — SQL Server rejects a negative <c>OFFSET</c> outright, so the request surfaces as a
/// 500 instead of the empty page that page genuinely holds.
/// </para>
/// <para>
/// This lived only inside <c>EntityQueryPipeline</c>, so handlers that paginate their own
/// queryable (the notification inbox and history reads) each re-derived it in 32-bit and kept
/// the overflow. Callers must route through here rather than open-coding the multiply.
/// </para>
/// </remarks>
public static class PagingMath
{
    /// <summary>
    /// Clamps a requested page into a safe <c>Skip</c>/<c>Take</c> pair.
    /// </summary>
    /// <param name="pageNumber">The one-based page number. Values below 1 are treated as page 1.</param>
    /// <param name="pageSize">The requested page size. Clamped into <c>[1, maxPageSize]</c>.</param>
    /// <param name="maxPageSize">The ceiling this caller enforces on page size.</param>
    /// <returns>
    /// The number of rows to skip and to take. A page beyond the reachable offset range yields
    /// <c>(0, 0)</c>, which materializes the empty page that page actually holds.
    /// </returns>
    public static (int Skip, int Take) Clamp(int pageNumber, int pageSize, int maxPageSize)
    {
        // A zero or negative page size would otherwise become Take(0) or a negative Take; a zero or
        // negative page number would become a negative Skip. Both arrive from callers outside the
        // API boundary's [Range] attributes, so neither can be assumed away here.
        var take = Math.Clamp(pageSize, 1, Math.Max(maxPageSize, 1));
        var page = Math.Max(pageNumber, 1);

        long skip = (long)take * (page - 1);

        return skip > int.MaxValue ? (0, 0) : ((int)skip, take);
    }
}
