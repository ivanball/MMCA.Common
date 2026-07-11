using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="OfflineBanner"/> (ADR-042): invisible while online (including the
/// always-online Blazor Server fallback), a warning alert while offline, live transitions in
/// both directions, and the monitoring bootstrap call.
/// </summary>
public sealed class OfflineBannerTests : BunitTestBase
{
    [Fact]
    public void WhileOnline_RendersNothing()
    {
        Services.AddSingleton<IConnectivityStatusService, AlwaysOnlineConnectivityStatusService>();

        var cut = RenderUnderTest<OfflineBanner>(_ => { });

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void WhileOffline_RendersTheLocalizedWarning()
    {
        var fake = new FakeConnectivityService { IsOnline = false };
        Services.AddSingleton<IConnectivityStatusService>(fake);

        var cut = RenderUnderTest<OfflineBanner>(_ => { });

        cut.Markup.Should().Contain("mmca-offline-banner");
        cut.Markup.Should().Contain("offline");
        fake.InitializeCalls.Should().BeGreaterThan(0, "the banner must start connectivity monitoring");
    }

    [Fact]
    public void ConnectivityTransitions_ToggleTheBannerBothWays()
    {
        var fake = new FakeConnectivityService { IsOnline = true };
        Services.AddSingleton<IConnectivityStatusService>(fake);

        var cut = RenderUnderTest<OfflineBanner>(_ => { });
        cut.Markup.Trim().Should().BeEmpty();

        fake.SetOnline(false);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("mmca-offline-banner"));

        fake.SetOnline(true);
        cut.WaitForAssertion(() => cut.Markup.Trim().Should().BeEmpty());
    }

    private sealed class FakeConnectivityService : IConnectivityStatusService
    {
        public event EventHandler? ConnectivityChanged;

        public bool IsOnline { get; set; } = true;

        public int InitializeCalls { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return ValueTask.CompletedTask;
        }

        public void SetOnline(bool isOnline)
        {
            IsOnline = isOnline;
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
