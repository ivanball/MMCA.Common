using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MMCA.Common.UI.Components.Lists;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Lists;

/// <summary>
/// bUnit tests for <see cref="InfiniteScrollSentinel"/>: the observer-only companion a page renders
/// when it owns its own card markup. What matters here is the contract with the shared
/// <c>infinite-scroll.js</c> module (the fixed <c>OnSentinelVisible</c> callback name), the
/// accessible progress row, and that disposal after the last page is silent.
/// </summary>
public sealed class InfiniteScrollSentinelTests : BunitTestBase
{
    private const string InfiniteScrollModulePath = "./_content/MMCA.Common.UI/infinite-scroll.js";

    [Fact]
    public void RendersTheSentinelElement()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(_ => { });

        cut.Find("div.infinite-scroll-sentinel").Should().NotBeNull();
    }

    [Fact]
    public void WhileIdle_RendersNoProgressRow()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p.Add(c => c.IsLoading, false));

        cut.FindAll("[role=status]").Should().BeEmpty();
    }

    [Fact]
    public void WhileLoading_RendersAPoliteProgressRow()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.IsLoading, true)
            .Add(c => c.LoadingLabel, "Loading more sessions"));

        var status = cut.Find("[role=status]");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.GetAttribute("aria-busy").Should().Be("true");
        cut.Markup.Should().Contain("Loading more sessions");
    }

    [Fact]
    public void WhenALoadFinishes_TheSentinelIsObservedAgain()
    {
        // L75: the observer reports threshold crossings only, so a sentinel still in view after the
        // host appended a page would never fire again; a finished load re-observes it.
        var module = JSInterop.SetupModule("./_content/MMCA.Common.UI/infinite-scroll.js");
        module.SetupVoid("observe", _ => true).SetVoidResult();
        module.SetupVoid("unobserve", _ => true).SetVoidResult();

        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p.Add(c => c.IsLoading, false));
        cut.WaitForAssertion(() => module.Invocations["observe"].Should().HaveCount(1));

        cut.Render(p => p.Add(c => c.IsLoading, true));
        cut.Render(p => p.Add(c => c.IsLoading, false));

        cut.WaitForAssertion(() => module.Invocations["observe"].Should().HaveCount(2));
    }

    [Fact]
    public async Task OnSentinelVisible_RaisesTheHostCallback()
    {
        var appended = 0;
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.OnVisible, EventCallback.Factory.Create(this, () => appended++)));

        await cut.Instance.OnSentinelVisible();

        appended.Should().Be(1);
    }

    [Fact]
    public async Task AfterDisposal_TheCallbackIsSuppressed()
    {
        // The host stops rendering the sentinel the moment the last page is loaded; an
        // observer callback still in flight must not re-enter the disposed component.
        var appended = 0;
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.OnVisible, EventCallback.Factory.Create(this, () => appended++)));

        await cut.Instance.DisposeAsync();
        await cut.Instance.OnSentinelVisible();

        appended.Should().Be(0);
    }

    // ── U-20 (ADC local test run 8) ──
    // The host stops rendering the sentinel the moment the last page arrives, which can land while
    // the module import is still in flight. The import's continuation must then see the disposal and
    // attach nothing; and an observe call the browser refuses (a JSException, e.g. the element is
    // already gone) must not escape the component: the list simply stops loading.
    [Fact]
    public async Task DisposedWhileTheModuleImportIsPending_ObservesNothing_WhenTheImportCompletes()
    {
        // bUnit answers a module import immediately (SetupModule) and refuses a hand-rolled
        // IJSObjectReference setup, so the import is held open by a runtime that delegates every
        // other call to bUnit's.
        var runtime = new PendingImportJSRuntime(JSInterop.JSRuntime, InfiniteScrollModulePath);
        Services.AddSingleton<IJSRuntime>(runtime);
        var lateModule = new Mock<IJSObjectReference>();

        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p.Add(c => c.IsLoading, false));
        await cut.WaitForAssertionAsync(() => runtime.ImportCalls.Should().Be(1, "the premise is a pending import"));

        await cut.Instance.DisposeAsync();
        var complete = async () =>
        {
            await cut.InvokeAsync(() => runtime.Import.SetResult(lateModule.Object));

            // Two dispatcher round trips: the import's continuation and anything it awaits in turn.
            await cut.InvokeAsync(() => { });
            await cut.InvokeAsync(() => { });
        };

        await complete.Should().NotThrowAsync("a late import must not fault the disposed component");
        Renderer.UnhandledException.IsCompleted.Should().BeFalse(
            "nothing may escape the after-render of a disposed sentinel");
        lateModule.Invocations
            .Where(i => i.Method.Name == nameof(IJSObjectReference.InvokeAsync))
            .Select(i => i.Arguments[0] as string)
            .Should().NotContain(
                "observe",
                "a sentinel disposed before its import resolved must not attach an observer nobody will ever detach");
    }

    [Fact]
    public void WhenObserveThrowsAJSException_NothingEscapesTheComponent()
    {
        var module = JSInterop.SetupModule(InfiniteScrollModulePath);
        module.SetupVoid("observe", _ => true).SetException(new JSException("Cannot read properties of null (reading 'isConnected')"));
        module.SetupVoid("unobserve", _ => true).SetVoidResult();

        var render = () => RenderUnderTest<InfiniteScrollSentinel>(p => p.Add(c => c.IsLoading, false));

        render.Should().NotThrow("a refused observe is best effort: the list stops loading, the page keeps rendering");
        module.Invocations["observe"].Should().HaveCount(1, "the premise is that observe was attempted and refused");
        Renderer.UnhandledException.IsCompleted.Should().BeFalse(
            "a JSException from observe must not reach the renderer's unhandled-exception path");
    }

    [Fact]
    public async Task DisposingTwiceIsSilent()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(_ => { });

        await cut.Instance.DisposeAsync();
        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Holds the one module import it was built for open until the test completes
    /// <see cref="Import"/>; every other call goes to bUnit's runtime unchanged.
    /// </summary>
    private sealed class PendingImportJSRuntime(IJSRuntime inner, string modulePath) : IJSRuntime
    {
        public TaskCompletionSource<IJSObjectReference> Import { get; } = new();

        public int ImportCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import"
                && args is [string path]
                && path == modulePath
                && typeof(TValue) == typeof(IJSObjectReference))
            {
                ImportCalls++;
                return new ValueTask<TValue>(AwaitImportAsync<TValue>());
            }

            return inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }

        private async Task<TValue> AwaitImportAsync<TValue>() => (TValue)await Import.Task;
    }
}
