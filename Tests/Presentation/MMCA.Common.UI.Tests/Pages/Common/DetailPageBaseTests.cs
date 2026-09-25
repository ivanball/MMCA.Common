using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.UI.Pages.Common;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Tests for <see cref="DetailPageBase"/>: the inline edit lifecycle an unsaved-changes guard reads,
/// and a dispose that cancels the page's in-flight work, retires its load guard, runs a derived
/// page's own cleanup and tolerates a second call.
/// </summary>
public sealed class DetailPageBaseTests : BunitTestBase
{
    [Fact]
    public void BeginEdit_OpensACleanEditor()
    {
        var page = new ProbePage();
        page.CallMarkDirty();

        page.CallBeginEdit();

        page.Editing.Should().BeTrue();
        page.Dirty.Should().BeFalse();
    }

    [Fact]
    public void MarkDirty_ThenEndEdit_ClosesTheEditorAndClearsTheFlag()
    {
        var page = new ProbePage();
        page.CallBeginEdit();
        page.CallMarkDirty();
        page.Dirty.Should().BeTrue();

        page.CallEndEdit();

        page.Editing.Should().BeFalse();
        page.Dirty.Should().BeFalse();
    }

    [Fact]
    public void ClearDirty_ClearsTheFlagAndLeavesTheEditorAsItWas()
    {
        var editing = new ProbePage();
        editing.CallBeginEdit();
        editing.CallMarkDirty();
        var reading = new ProbePage();
        reading.CallMarkDirty();

        editing.CallClearDirty();
        reading.CallClearDirty();

        editing.Dirty.Should().BeFalse();
        editing.Editing.Should().BeTrue();
        reading.Dirty.Should().BeFalse();
        reading.Editing.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CancelsThePageTokenAndRetiresTheLoadGuard()
    {
        var page = new ProbePage();
        var token = page.Token;
        var (loadToken, generation) = page.Guard.Begin();

        page.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        loadToken.IsCancellationRequested.Should().BeTrue();
        page.Guard.IsCurrent(generation).Should().BeFalse();
    }

    [Fact]
    public void Dispose_RunsTheDerivedHookOnceAndToleratesASecondCall()
    {
        var page = new ProbePage();

        page.Dispose();
        var act = page.Dispose;

        act.Should().NotThrow();
        page.DisposeCalls.Should().Be(2);
        page.ReleasedOwnResources.Should().Be(1);
    }

    [Fact]
    public async Task RenderedPage_IsDisposedWithTheRenderer()
    {
        var cut = RenderUnderTest<ProbePage>(_ => { });
        var token = cut.Instance.Token;

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue();
    }

    private sealed class ProbePage : DetailPageBase
    {
        private bool _released;

        public int DisposeCalls { get; private set; }

        public int ReleasedOwnResources { get; private set; }

        public bool Editing => IsEditing;

        public bool Dirty => IsDirty;

        public CancellationToken Token => PageToken;

        public MMCA.Common.UI.Common.LatestLoadGuard Guard => LoadGuard;

        public void CallBeginEdit() => BeginEdit();

        public void CallEndEdit() => EndEdit();

        public void CallMarkDirty() => MarkDirty();

        public void CallClearDirty() => ClearDirty();

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, "probe");

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            if (disposing && !_released)
            {
                _released = true;
                ReleasedOwnResources++;
            }

            base.Dispose(disposing);
        }
    }
}
