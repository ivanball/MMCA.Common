using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Services;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.LoadTests.Support;

/// <summary>
/// One stack seeded with <see cref="RowCount"/> <see cref="LoadItem"/> rows, shared by the paging and
/// concurrency scenarios. Seeding goes through the framework's own context, so the audit interceptor
/// stamps every row exactly as a real write would; it is setup, not a measured scenario, and its
/// duration is reported for reference only.
/// </summary>
public sealed class PagedQueryFixture : IAsyncLifetime
{
    public const int RowCount = 100_000;

    private const int SeedChunkSize = 5_000;

    private LoadStack? _stack;

    internal LoadStack Stack => _stack ?? throw new InvalidOperationException("Fixture not initialized.");

    public TimeSpan SeedDuration { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _stack = await LoadStack.CreateAsync(
            "paging",
            services =>
            {
                services.AddSingleton<IEntityDTOMapper<LoadItem, LoadItemDTO, int>, LoadItemMapper>();
                services.AddSingleton<INavigationPopulator<LoadItem>, LoadItemNavigationPopulator>();
                services.AddScoped<IEntityQueryService<LoadItem, LoadItemDTO, int>, EntityQueryService<LoadItem, LoadItemDTO, int>>();
            },
            ct);

        var seed = Stopwatch.StartNew();
        for (var start = 1; start <= RowCount; start += SeedChunkSize)
        {
            await using var scope = _stack.Services.CreateAsyncScope();
            var context = LoadStack.GetContext(scope.ServiceProvider);
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.Set<LoadItem>().AddRange(Enumerable.Range(start, SeedChunkSize).Select(LoadItem.ForId));
            await context.SaveChangesAsync(ct);
        }

        SeedDuration = seed.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs one paged read the way a request does: a fresh scope, the query service resolved from it,
    /// and <c>GetAllAsync</c> with the dynamic filter/sort/page parameters. The measured time covers
    /// the scope, the resolution and the query.
    /// </summary>
    /// <param name="query">The query shape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page and the elapsed milliseconds.</returns>
    internal async Task<(PagedCollectionResult<object> Page, double Milliseconds)> RunAsync(
        PagedQuery query,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        await using var scope = Stack.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IEntityQueryService<LoadItem, LoadItemDTO, int>>();
        var result = await service.GetAllAsync(
            filters: query.Filters,
            sortColumn: query.SortColumn,
            sortDirection: query.SortDirection,
            pageNumber: query.PageNumber,
            pageSize: query.PageSize,
            cancellationToken: cancellationToken);
        timer.Stop();

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Query '{query.Name}' failed: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        }

        return (result.Value!, timer.Elapsed.TotalMilliseconds);
    }
}

/// <summary>One paged query shape.</summary>
/// <param name="Name">A label for results and failure messages.</param>
/// <param name="PageNumber">The 1-based page.</param>
/// <param name="PageSize">The page size.</param>
/// <param name="SortColumn">The DTO sort column, or null for the default (Id) order.</param>
/// <param name="SortDirection">asc or desc, or null.</param>
/// <param name="Filters">The dynamic filters, or null.</param>
internal sealed record PagedQuery(
    string Name,
    int PageNumber,
    int PageSize,
    string? SortColumn = null,
    string? SortDirection = null,
    Dictionary<string, (string Operator, string Value)>? Filters = null);
