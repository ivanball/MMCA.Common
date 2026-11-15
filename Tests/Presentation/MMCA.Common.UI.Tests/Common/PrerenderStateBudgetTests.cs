using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// The page-wide half of the prerender budget (Store manual test Run 4, O8): several components persist
/// into one circuit-start message, so a per-key cap alone let two keys just under it add up to 28.5 KB
/// of SignalR's 32 KB. The running total is per <see cref="PersistentComponentState"/>, so one page's
/// state never counts against another's.
/// </summary>
public sealed class PrerenderStateBudgetTests : BunitTestBase
{
    // A JSON string of n characters is n + 2 bytes (the quotes): 10 KB each, under the 14 KB per-key cap,
    // 20 KB together, over the 16 KB page-wide budget.
    private static readonly string TenKilobytes = new('x', 10 * 1024);

    [Fact]
    public void TwoTenKilobyteKeys_OnOneState_TheSecondIsSkipped()
    {
        var bunitState = AddBunitPersistentComponentState();
        var state = Services.GetRequiredService<PersistentComponentState>();
        bool? first = null;
        bool? second = null;
        state.RegisterOnPersisting(
            () =>
            {
                first = state.TryPersistAsJsonWithinBudget("first", TenKilobytes);
                second = state.TryPersistAsJsonWithinBudget("second", TenKilobytes);
                return Task.CompletedTask;
            },
            RenderMode.InteractiveAuto);

        bunitState.TriggerOnPersisting();

        first.Should().BeTrue("10 KB fits both the per-key cap and the empty page-wide total");
        second.Should().BeFalse("the second 10 KB would take the page past its 16 KB total");
        bunitState.TryTake<JsonElement>("first", out _).Should().BeTrue();
        bunitState.TryTake<JsonElement>("second", out _).Should().BeFalse("a skipped key fetches on the interactive pass");
    }

    [Fact]
    public void SmallKeys_UnderTheTotal_AllPersist()
    {
        var bunitState = AddBunitPersistentComponentState();
        var state = Services.GetRequiredService<PersistentComponentState>();
        var outcomes = new List<bool>();
        state.RegisterOnPersisting(
            () =>
            {
                outcomes.Add(state.TryPersistAsJsonWithinBudget("a", new string('a', 4 * 1024)));
                outcomes.Add(state.TryPersistAsJsonWithinBudget("b", new string('b', 4 * 1024)));
                outcomes.Add(state.TryPersistAsJsonWithinBudget("c", new string('c', 4 * 1024)));
                return Task.CompletedTask;
            },
            RenderMode.InteractiveAuto);

        bunitState.TriggerOnPersisting();

        outcomes.Should().Equal(true, true, true);
    }

    [Fact]
    public void SeparateStateInstances_DoNotShareATotal()
    {
        var bunitState = AddBunitPersistentComponentState();
        var state = Services.GetRequiredService<PersistentComponentState>();
        using var otherContext = new BunitContext();
        var otherBunitState = otherContext.AddBunitPersistentComponentState();
        var otherState = otherContext.Services.GetRequiredService<PersistentComponentState>();
        otherState.Should().NotBeSameAs(state);

        bool? persisted = null;
        bool? otherPersisted = null;
        state.RegisterOnPersisting(
            () =>
            {
                persisted = state.TryPersistAsJsonWithinBudget("page", TenKilobytes);
                return Task.CompletedTask;
            },
            RenderMode.InteractiveAuto);
        otherState.RegisterOnPersisting(
            () =>
            {
                otherPersisted = otherState.TryPersistAsJsonWithinBudget("page", TenKilobytes);
                return Task.CompletedTask;
            },
            RenderMode.InteractiveAuto);

        bunitState.TriggerOnPersisting();
        otherBunitState.TriggerOnPersisting();

        persisted.Should().BeTrue();
        otherPersisted.Should().BeTrue("another page's state starts its own total at zero");
    }
}
