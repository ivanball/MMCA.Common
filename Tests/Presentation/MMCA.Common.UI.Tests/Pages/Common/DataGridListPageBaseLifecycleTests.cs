using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// The first-render lifecycle of <see cref="DataGridListPageBase{TDto}"/>: a page disposed while its
/// first-render interop round trips are still in flight releases what those continuations create
/// (M236), and the late <c>sessionStorage</c> restore reaches a page that has no <c>GridRef</c>
/// through <c>OnRestoredStateAppliedAsync</c>.
/// </summary>
public sealed class DataGridListPageBaseLifecycleTests : BunitTestBase
{
    private const string ScrollModulePath = "./_content/MMCA.Common.UI/list-page-scroll.js";
    private const string NavInteropModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    private readonly Mock<IBrowserViewportService> _viewport = new();
    private readonly List<string> _viewportCalls = [];
    private readonly GatedScrollImportJSRuntime _js;

    public DataGridListPageBaseLifecycleTests()
    {
        Services.AddSingleton(new Mock<IToastService>().Object);
        _viewport
            .Setup(v => v.UnsubscribeAsync(It.IsAny<IBrowserViewportObserver>()))
            .Callback(() => _viewportCalls.Add("unsubscribe"))
            .Returns(Task.CompletedTask);
        Services.AddSingleton(_viewport.Object);

        // Last registration wins: everything still reaches the bUnit JSInterop except, when a test
        // opts in, the scroll-module import, which bUnit cannot hold open itself.
        _js = new GatedScrollImportJSRuntime(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(_js);
        ConfigureDataGridListPageHost(stubViewport: false);
    }

    public sealed record Row(int Id, string Name);

    private sealed class CardListPage : DataGridListPageBase<Row>
    {
        public List<bool> RestoredStateCalls { get; } = [];

        public string? RestoredSearch { get; private set; }

        protected override string Title => "Cards";

        protected override void RestoreFilters(IReadOnlyDictionary<string, string> filters) =>
            RestoredSearch = filters.TryGetValue("search", out var search) ? search : null;

        protected override Task OnRestoredStateAppliedAsync(bool needsReload)
        {
            RestoredStateCalls.Add(needsReload);
            return Task.CompletedTask;
        }
    }

    // == M236: setup that completes after disposal is released ==
    [Fact]
    public async Task FirstRender_WhenDisposedDuringTheScrollModuleImport_ReleasesTheModuleAndNeverStartsTracking()
    {
        _viewport
            .Setup(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()))
            .Callback(() => _viewportCalls.Add("subscribe"))
            .Returns(Task.CompletedTask);
        _js.GateScrollImport = true;
        var module = new Mock<IJSObjectReference>();
        module
            .Setup(m => m.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(It.IsAny<string>(), It.IsAny<object?[]?>()))
            .Returns(new ValueTask<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(default(Microsoft.JSInterop.Infrastructure.IJSVoidResult)!));
        module.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var cut = Render<CardListPage>();
        await cut.WaitForStateAsync(() => _js.ScrollImportRequested);

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        _viewportCalls.Clear();
        await cut.InvokeAsync(() => _js.ScrollImport.SetResult(module.Object));

        await cut.WaitForAssertionAsync(() => module.Verify(m => m.DisposeAsync(), Times.Once));
        module.Verify(
            m => m.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("enableScrollTracking", It.IsAny<object?[]?>()),
            Times.Never,
            "a disposed page must not register a window scroll listener that writes under its route");
        _viewportCalls.Should().Equal(["unsubscribe"], "the subscription taken before disposal completed is released");
        cut.Instance.RestoredStateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstRender_WhenDisposedDuringTheViewportSubscribe_UnsubscribesAndImportsNothing()
    {
        var subscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _viewport
            .Setup(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()))
            .Returns(subscribe.Task);

        var cut = Render<CardListPage>();
        await cut.WaitForAssertionAsync(() => _viewport.Verify(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()), Times.Once));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        _viewportCalls.Clear();
        await cut.InvokeAsync(subscribe.SetResult);

        await cut.WaitForAssertionAsync(() => _viewportCalls.Should().Equal(["unsubscribe"], "the subscription that completed after disposal is released"));
        JSInterop.Invocations
            .Where(i => i.Identifier == "import" && i.Arguments.Count > 0 && Equals(i.Arguments[0], ScrollModulePath))
            .Should().BeEmpty("a disposed page must not import the scroll module");
    }

    // == The late-restore hook for a page with no GridRef ==
    [Fact]
    public async Task FirstRender_WhenSessionStorageRestoresStateLate_CallsTheHookWithNeedsReloadOnAGridlessPage()
    {
        _viewport
            .Setup(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var navInterop = JSInterop.SetupModule(NavInteropModulePath);
        navInterop
            .Setup<ListPageState?>("sessionGet", "mmca.lps:/")
            .SetResult(new ListPageState { Filters = new Dictionary<string, string> { ["search"] = "shirt" } });
        JSInterop.SetupModule(ScrollModulePath);

        var cut = Render<CardListPage>();

        await cut.WaitForAssertionAsync(() => cut.Instance.RestoredStateCalls.Should().Equal(true));
        cut.Instance.RestoredSearch.Should().Be("shirt", "the hook runs after the late state was applied");
    }

    [Fact]
    public Task FirstRender_WithNothingToRestoreLate_CallsTheHookOnceWithoutNeedsReload()
    {
        _viewport
            .Setup(v => v.SubscribeAsync(It.IsAny<IBrowserViewportObserver>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        JSInterop.SetupModule(ScrollModulePath);

        var cut = Render<CardListPage>();

        return cut.WaitForAssertionAsync(() => cut.Instance.RestoredStateCalls.Should().Equal(false));
    }

    /// <summary>
    /// Delegates every call to the bUnit runtime, except that with <see cref="GateScrollImport"/> set
    /// the scroll-module import stays pending until the test completes <see cref="ScrollImport"/>.
    /// </summary>
    private sealed class GatedScrollImportJSRuntime(IJSRuntime inner) : IJSRuntime
    {
        public bool GateScrollImport { get; set; }

        public bool ScrollImportRequested { get; private set; }

        public TaskCompletionSource<IJSObjectReference> ScrollImport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (GateScrollImport && identifier == "import" && args is [string path, ..] && path == ScrollModulePath)
            {
                ScrollImportRequested = true;
                return new ValueTask<TValue>(AwaitScrollImportAsync<TValue>());
            }

            return inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }

        private async Task<TValue> AwaitScrollImportAsync<TValue>() => (TValue)await ScrollImport.Task;
    }
}
