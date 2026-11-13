using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Components.Auditing;

/// <summary>
/// Renders an entity's recorded changes (ADR-075) as a table: when, who, what kind of change, which
/// field, the value before and the value after. Place it on a detail page inside an
/// <c>AuthorizeView</c> on the app's audit permission, and feed it a page fetcher, usually
/// <c>IAuditHistoryUIService.GetPageAsync</c> with the host's route prefix, entity alias and key.
/// </summary>
/// <remarks>
/// Nothing is fetched while the renderer is not interactive; the first interactive initialization
/// fetches page 1, and a full page offers "Load more". Every fetch takes a token cancelled when it is
/// superseded (<see cref="ReloadAsync"/>) or the panel is disposed, and a superseded response is
/// discarded.
/// </remarks>
public partial class AuditHistoryPanel : IDisposable
{
    private readonly List<AuditHistoryEntryDTO> _entries = [];
    private CancellationTokenSource? _fetchCts;
    private int _generation;
    private int _loadedPage;
    private int? _failedPage;
    private bool _hasMore;
    private bool _firstPageEmpty;
    private bool _isLoading;
    private bool _disposed;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <summary>
    /// Gets or sets the page fetcher: 1-based page number, page size and a cancellation token in, the
    /// page's entries (newest first) or a failure out. Required.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public Func<int, int, CancellationToken, Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>>> FetchPage { get; set; } = default!;

    /// <summary>Gets or sets the page size. Defaults to 20.</summary>
    [Parameter] public int PageSize { get; set; } = 20;

    /// <summary>Gets or sets the panel heading; defaults to the shared <c>AuditHistory.Title</c> text.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Gets or sets how a user id is shown; <see langword="null"/> shows the id itself.</summary>
    [Parameter] public Func<UserIdentifierType, string>? UserLabel { get; set; }

    /// <summary>Gets or sets how a property name is shown; <see langword="null"/> shows the name itself.</summary>
    [Parameter] public Func<string, string>? PropertyLabel { get; set; }

    /// <summary>Gets or sets the time zone the change times are shown in; <see langword="null"/> shows UTC.</summary>
    [Parameter] public TimeZoneInfo? DisplayTimeZone { get; set; }

    /// <summary>
    /// Clears the rows, cancels any fetch in flight and fetches page 1 again. A response for the
    /// superseded fetch is discarded.
    /// </summary>
    /// <returns>A task that completes when page 1 has been fetched.</returns>
    public Task ReloadAsync()
    {
        _entries.Clear();
        _loadedPage = 0;
        _hasMore = false;
        _firstPageEmpty = false;
        _failedPage = null;
        return LoadPageAsync(1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels the fetch in flight. Overrides release their own resources and then call the base.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
        }

        _disposed = true;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (FetchPage is null)
        {
            throw new InvalidOperationException("AuditHistoryPanel requires a FetchPage delegate.");
        }
    }

    /// <inheritdoc />
    /// <remarks>A missing <see cref="FetchPage"/> fetches nothing here; <see cref="OnParametersSet"/> reports it.</remarks>
    protected override Task OnInitializedAsync() =>
        RendererInfo.IsInteractive && FetchPage is not null ? LoadPageAsync(1) : Task.CompletedTask;

    private Task LoadMoreAsync() => LoadPageAsync(_loadedPage + 1);

    private Task RetryAsync() => _failedPage is { } page ? LoadPageAsync(page) : Task.CompletedTask;

    private async Task LoadPageAsync(int page)
    {
        if (_disposed)
        {
            return;
        }

        // The generation moves first, so a superseded response that lands while the cancel below
        // runs is already stale.
        var generation = ++_generation;
        var previous = _fetchCts;
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        var token = cts.Token;
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
            if (generation != _generation || _disposed)
            {
                return;
            }
        }

        _isLoading = true;
        _failedPage = null;
        StateHasChanged();

        try
        {
            var result = await FetchPage(page, PageSize, token);
            if (generation != _generation)
            {
                return;
            }

            if (!result.TryGetValue(out var entries))
            {
                _failedPage = page;
                return;
            }

            _entries.AddRange(entries);
            _loadedPage = page;
            _hasMore = entries.Count == PageSize;
            _firstPageEmpty = page == 1 && entries.Count == 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or disposed: the newer fetch (or nobody) owns the panel now.
        }
        finally
        {
            if (generation == _generation)
            {
                _isLoading = false;
                if (!_disposed)
                {
                    StateHasChanged();
                }
            }
        }
    }

    private string WhenText(AuditHistoryEntryDTO entry)
    {
        var zone = DisplayTimeZone ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entry.ChangedOn, DateTimeKind.Utc), zone);
        return $"{local.ToString("g", CultureInfo.CurrentCulture)} {zone.StandardName}";
    }

    private string WhoText(AuditHistoryEntryDTO entry)
    {
        if (entry.ChangedBy is not { } userId)
        {
            return L["AuditHistory.System"];
        }

        return UserLabel?.Invoke(userId) ?? string.Create(CultureInfo.InvariantCulture, $"{userId}");
    }

    private string FieldText(AuditHistoryEntryDTO entry)
    {
        if (entry.PropertyName is not { } name)
        {
            return string.Empty;
        }

        return PropertyLabel?.Invoke(name) ?? name;
    }
}
