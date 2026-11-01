using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="AppLockSettingCard"/>, the opt-in switch <see cref="BiometricGate"/> enforces:
/// the switch shows only where a biometric or screen lock exists, turning the lock on stores
/// <see cref="DevicePreferenceKeys.AppLockEnabled"/> only after a successful check, a refused check
/// leaves it off, and turning it off needs no check.
/// </summary>
public sealed class AppLockSettingCardTests : BunitTestBase
{
    private readonly Mock<IBiometricAuthenticator> _biometrics = new();
    private readonly Dictionary<string, object?> _stored = new(StringComparer.Ordinal);

    public AppLockSettingCardTests()
    {
        _biometrics.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var preferences = new Mock<IDevicePreferences>();
        preferences
            .Setup(p => p.GetAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, bool fallback, CancellationToken _) =>
                _stored.TryGetValue(key, out var value) && value is bool stored ? stored : fallback);
        preferences
            .Setup(p => p.SetAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string key, bool value, CancellationToken _) => _stored[key] = value)
            .Returns(Task.CompletedTask);

        Services.AddSingleton(_biometrics.Object);
        Services.AddSingleton(preferences.Object);
    }

    [Fact]
    public void WhenTheDeviceHasNoBiometricOrScreenLock_TheCardExplainsInsteadOfOfferingASwitch()
    {
        _biometrics.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var cut = RenderUnderTest<AppLockSettingCard>(_ => { });

        cut.FindComponents<MudSwitch<bool>>().Should().BeEmpty();
        cut.FindAll(".mud-typography-body2").Should().ContainSingle("the card explains why there is no switch");
    }

    [Fact]
    public async Task TurningTheLockOn_StoresThePreferenceOnlyAfterASuccessfulCheck()
    {
        _biometrics.Setup(b => b.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cut = RenderUnderTest<AppLockSettingCard>(_ => { });

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(true));

        _stored.Should().ContainKey(DevicePreferenceKeys.AppLockEnabled).WhoseValue.Should().Be(true);
        _biometrics.Verify(b => b.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARefusedCheck_LeavesTheLockOff()
    {
        _biometrics.Setup(b => b.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var cut = RenderUnderTest<AppLockSettingCard>(_ => { });

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(true));

        _stored.Should().NotContainKey(DevicePreferenceKeys.AppLockEnabled);
    }

    [Fact]
    public async Task TurningTheLockOff_NeedsNoCheck()
    {
        _stored[DevicePreferenceKeys.AppLockEnabled] = true;
        var cut = RenderUnderTest<AppLockSettingCard>(_ => { });

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));

        _stored[DevicePreferenceKeys.AppLockEnabled].Should().Be(false);
        _biometrics.Verify(b => b.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
