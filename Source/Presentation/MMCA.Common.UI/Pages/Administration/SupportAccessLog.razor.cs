using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Services.Administration;
using MMCA.Common.UI.Services.Culture;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The support access log (ADR-137): every read-only view-as grant, who viewed whom, why (in full),
/// when, and how it ended (live, ended, expired), newest first, filtered and paged on the server.
/// It is a COMPONENT, not a page: the app supplies the route and the authorization (the endpoint
/// requires <c>users:manage</c>), so who may reach it stays the app's decision.
/// </summary>
/// <remarks>
/// <para>
/// A party with no email snapshot shows the "Email not on record" placeholder: erasure blanks the
/// snapshot (the access itself stays on record until the retention sweep), and a grant issued
/// before the snapshot shipped never had one.
/// </para>
/// <para>
/// Every string it renders is looked up in <see cref="Localizer"/> FIRST and falls back to
/// <see cref="SupportAccessLogResources"/>, the <see cref="RoleAdminList"/> contract. The date filters
/// are whole days on the VIEWER's clock (<see cref="ViewerTimeZone"/>): From sends the start of its
/// local day converted to UTC, and To includes its day too (the request sends the start of the next
/// local day, in UTC, as the exclusive bound). Until the browser's zone is known the days are UTC.
/// </para>
/// </remarks>
public partial class SupportAccessLog : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<ViewAsGrantSummaryResponse> _grants = [];
    private ViewAsGrantListRequest _applied = new();
    private int _currentPage = 1;
    private int _totalPages = 1;
    private bool _disposed;

    private string? _staffFilter;
    private string? _viewedFilter;
    private DateTime? _fromFilter;
    private DateTime? _toFilter;
    private ViewAsGrantStatus? _statusFilter;

    /// <summary>The last load attempt's outcome; a failure is rendered inline with a retry.</summary>
    private Result? _loadResult;

    /// <summary>The page title. Defaults to the localized "Support access log" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="SupportAccessLogResources"/>.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    /// <summary>Rows per page (clamped by the server to <see cref="ViewAsGrantListRequest.MaxPageSize"/>).</summary>
    [Parameter] public int PageSize { get; set; } = ViewAsGrantListRequest.DefaultPageSize;

    /// <summary>True while a page is loading.</summary>
    protected bool IsLoading { get; private set; }

    [Inject] private IViewAsGrantsUIService Grants { get; set; } = default!;

    [Inject] private IStringLocalizer<SupportAccessLogResources> L { get; set; } = default!;

    [Inject] private ViewerTimeZone ViewerTime { get; set; } = default!;

    private string Title => Heading ?? T("Title");

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadPageAsync();

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Times render on the viewer's clock; the zone is only readable once JS is available.
        if (firstRender && !_disposed && await ViewerTime.EnsureResolvedAsync(_cts.LifetimeToken()))
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Releases the cancellation source that supersedes an in-flight load when the component goes
    /// away.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }

    private static Color StatusColor(ViewAsGrantStatus status) => status switch
    {
        ViewAsGrantStatus.Live => Color.Warning,
        ViewAsGrantStatus.Ended => Color.Success,
        ViewAsGrantStatus.Expired => Color.Default,
        _ => Color.Default,
    };

    /// <summary>
    /// The UTC instant the viewer's local day <paramref name="date"/> starts at, shifted by
    /// <paramref name="addDays"/> whole local days first (so To's exclusive bound is the start of the
    /// NEXT local day, which is not always 24 hours later across a daylight-saving change).
    /// <see cref="ViewerTimeZone.Zone"/> is UTC until the browser answers, which keeps the UTC-day
    /// behaviour then. <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> never throws on a local
    /// midnight a spring-forward skips: it returns the standard offset, which lands on the first
    /// instant of that day.
    /// </summary>
    private DateTime? StartOfViewerDayUtc(DateTime? date, int addDays = 0)
    {
        if (date is not { } d)
        {
            return null;
        }

        var localMidnight = DateTime.SpecifyKind(d.Date.AddDays(addDays), DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, ViewerTime.Zone.GetUtcOffset(localMidnight)).UtcDateTime;
    }

    /// <summary>
    /// Resolves one key: the app's own localizer when it carries the key, otherwise this component's
    /// resources.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The resolved string.</returns>
    private string T(string key)
    {
        if (Localizer is not null)
        {
            var overridden = Localizer[key];
            if (!overridden.ResourceNotFound)
            {
                return overridden.Value;
            }
        }

        return L[key].Value;
    }

    private string StatusText(ViewAsGrantStatus status) => T($"Status.{status}");

    private string StatusFilterText(ViewAsGrantStatus? status) =>
        status is { } value ? StatusText(value) : T("Filter.AnyStatus");

    private string Display(string? email) => email ?? T("Display.NotOnRecord");

    private async Task ApplyFiltersAsync()
    {
        _applied = new ViewAsGrantListRequest
        {
            Staff = string.IsNullOrWhiteSpace(_staffFilter) ? null : _staffFilter.Trim(),
            Viewed = string.IsNullOrWhiteSpace(_viewedFilter) ? null : _viewedFilter.Trim(),
            From = StartOfViewerDayUtc(_fromFilter),
            To = StartOfViewerDayUtc(_toFilter, addDays: 1),
            Status = _statusFilter,
        };
        _currentPage = 1;
        await LoadPageAsync();
    }

    private Task ClearFiltersAsync()
    {
        _staffFilter = null;
        _viewedFilter = null;
        _fromFilter = null;
        _toFilter = null;
        _statusFilter = null;
        return ApplyFiltersAsync();
    }

    /// <summary>
    /// Moves to <paramref name="page"/>. A failed load renders the inline failure and its retry, and
    /// the pager goes back to the page the retained rows belong to.
    /// </summary>
    private async Task OnPageChangedAsync(int page)
    {
        var previous = _currentPage;
        _currentPage = page;
        if (!await LoadPageAsync())
        {
            _currentPage = previous;
        }
    }

    /// <summary>Loads <see cref="_currentPage"/> under the applied filters, sized by the server's total.</summary>
    /// <returns><see langword="true"/> when the page loaded.</returns>
    private async Task<bool> LoadPageAsync()
    {
        IsLoading = true;
        _loadResult = null;

        try
        {
            var request = _applied with { PageNumber = _currentPage, PageSize = PageSize };
            var result = await Grants.GetPageAsync(request, _cts.LifetimeToken());
            _loadResult = result;
            if (!result.TryGetValue(out var page))
            {
                return false;
            }

            _grants = [.. page.Items];
            _totalPages = Math.Max(1, (int)Math.Ceiling((double)page.PaginationMetadata.TotalItemCount / Math.Max(1, PageSize)));
            return true;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
