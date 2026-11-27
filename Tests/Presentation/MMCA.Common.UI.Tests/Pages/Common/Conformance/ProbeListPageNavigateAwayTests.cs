using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// A-23 (ADC local test run 21): a signed-out reader on the open public list followed an app shortcut to
/// the same route with <c>?mine=true</c>. The derived page redirected to sign in, but on that same location
/// change the base reloaded the list and mirrored its state to the URL with a replace navigation, and on
/// Blazor Server that replace landed after the redirect and cancelled it. The derived page now leaves
/// through <c>NavigateAway</c>, after which the base neither reloads nor rewrites the URL.
/// </summary>
public sealed class ProbeListPageNavigateAwayTests : BunitTestBase
{
    private const string LoginTarget = "/login?returnUrl=%2Fprobes%3Fmine%3Dtrue";

    private readonly Mock<IProbeRowClient> _client = new();
    private readonly ServerLikeNavigationManager _navigation = new();
    private TaskCompletionSource? _fetchGate;

    public ProbeListPageNavigateAwayTests()
    {
        Services.AddSingleton(_client.Object);
        _client
            .Setup(c => c.GetPagedAsync(
                It.IsAny<Dictionary<string, (string Operator, string Value)>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => AnswerAsync());
        Services.AddSingleton<NavigationManager>(_navigation);
        ConfigureDataGridListPageHost();
    }

    [Fact]
    public async Task ARedirectOnASameRouteNavigation_EndsOnTheRedirectTarget_WithNoReplaceNavigationAfterIt()
    {
        var cut = await RenderLoadedPageAsync<RedirectsBeforeTheBasePage>();
        var fetchesBefore = Fetches();

        await cut.InvokeAsync(() => _navigation.BrowserNavigatesTo("/probes?mine=true"));
        await cut.InvokeAsync(() => { });

        _navigation.Held.Should().Equal([(LoginTarget, false)], "the redirect is the last navigation the browser applies");
        Fetches().Should().Be(fetchesBefore, "a page that is leaving does not reload its list");
    }

    // The other order: the base's handler already started its reload when the page redirects, and the
    // reload's save lands after the redirect. The URL rewrite is what has to stay out.
    [Fact]
    public async Task ARedirectWhileTheBaseReloadIsInFlight_IssuesNoReplaceNavigationAfterIt()
    {
        var cut = await RenderLoadedPageAsync<RedirectsAfterTheBasePage>();
        _fetchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await cut.InvokeAsync(() => _navigation.BrowserNavigatesTo("/probes?mine=true"));
        await cut.InvokeAsync(() => _fetchGate.SetResult());
        await cut.InvokeAsync(() => { });

        _navigation.Held.Should().Equal([(LoginTarget, false)], "the reload's save must not rewrite the URL after the redirect");
    }

    private async Task<IRenderedComponent<TPage>> RenderLoadedPageAsync<TPage>()
        where TPage : ProbeListPage
    {
        RenderMudProviders();
        var cut = RenderUnderTest<TPage>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Row 1"), TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        // From here the browser has not answered yet: navigations queue as on Blazor Server.
        _navigation.Hold = true;
        return cut;
    }

    private int Fetches() => _client.Invocations.Count(i => i.Method.Name == nameof(IProbeRowClient.GetPagedAsync));

    private async Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>> AnswerAsync()
    {
        if (_fetchGate is { } gate)
        {
            await gate.Task;
        }

        return Result.Success<(IReadOnlyList<ProbeRow> Items, int TotalItems)>(([new ProbeRow(1, "Row 1")], 1));
    }

    /// <summary>Redirects a <c>?mine=true</c> arrival, hearing the location change before the base does.</summary>
    public sealed class RedirectsBeforeTheBasePage : ProbeListPage
    {
        [Inject] private NavigationManager Navigation { get; set; } = default!;

        protected override void OnInitialized()
        {
            Navigation.LocationChanged += RedirectMineArrival;
            base.OnInitialized();
        }

        private void RedirectMineArrival(object? sender, LocationChangedEventArgs e)
        {
            if (e.Location.Contains("mine=true", StringComparison.Ordinal))
            {
                NavigateAway(LoginTarget);
            }
        }
    }

    /// <summary>Redirects a <c>?mine=true</c> arrival, hearing the location change after the base does.</summary>
    public sealed class RedirectsAfterTheBasePage : ProbeListPage
    {
        [Inject] private NavigationManager Navigation { get; set; } = default!;

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Navigation.LocationChanged += RedirectMineArrival;
        }

        private void RedirectMineArrival(object? sender, LocationChangedEventArgs e)
        {
            if (e.Location.Contains("mine=true", StringComparison.Ordinal))
            {
                NavigateAway(LoginTarget);
            }
        }
    }

    /// <summary>
    /// A navigation manager that, once <see cref="Hold"/> is set, behaves like Blazor Server's: a
    /// <c>NavigateTo</c> is sent to the browser and the URI moves only when the browser confirms, so a
    /// navigation started in the meantime still sees the old URI. Held navigations are recorded in the
    /// order the browser would apply them. Before that it applies each navigation at once (as bUnit's
    /// own manager does), so the page's first-load URL writes echo and settle.
    /// </summary>
    private sealed class ServerLikeNavigationManager : NavigationManager
    {
        public ServerLikeNavigationManager() => Initialize("http://localhost/", "http://localhost/probes");

        public bool Hold { get; set; }

        public List<(string Uri, bool Replace)> Held { get; } = [];

        public void BrowserNavigatesTo(string relativeUri)
        {
            Uri = ToAbsoluteUri(relativeUri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            if (Hold)
            {
                Held.Add((uri, options.ReplaceHistoryEntry));
                return;
            }

            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
