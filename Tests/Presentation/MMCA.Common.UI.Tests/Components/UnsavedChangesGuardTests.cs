using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Routing;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

public sealed class UnsavedChangesGuardTests : BunitTestBase
{
    [Fact]
    public void Accessor_TakesPrecedence_OverIsDirtyParameter()
    {
        // IsDirty=false but the live accessor reports dirty — the guard must honor the accessor,
        // which is the whole point of the param-lag fix.
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, false)
            .Add(c => c.IsDirtyAccessor, () => true));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue();
    }

    [Fact]
    public void FallsBackToIsDirty_WhenNoAccessor()
    {
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, true));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue();
    }
}
