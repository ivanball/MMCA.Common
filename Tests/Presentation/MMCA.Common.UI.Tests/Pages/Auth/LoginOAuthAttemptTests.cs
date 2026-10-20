using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// M237: the OAuth attempt the completion page requires back is started when the user follows a
/// web provider button, not when <c>/login</c> renders. A login page left open past the attempt
/// lifetime, or a second login tab in the same browser profile, must still complete the sign-in the
/// user started with the click.
/// </summary>
public sealed class LoginOAuthAttemptTests : BunitTestBase
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly OAuthFlowStateStore _flowState;

    public LoginOAuthAttemptTests()
    {
        _flowState = new OAuthFlowStateStore(new InMemoryCacheStore(), _clock);
        var settings = new Mock<IOAuthUISettings>();
        settings.SetupGet(s => s.GoogleEnabled).Returns(true);

        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(_flowState);
        Services.AddSingleton(settings.Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
    }

    [Fact]
    public async Task ProviderButton_FollowedAfterTheLoginPageSatIdlePastTheAttemptLifetime_StillCompletes()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        _clock.Advance(TimeSpan.FromMinutes(11));
        var url = await FollowGoogleButtonAsync(cut);
        _clock.Advance(TimeSpan.FromMinutes(1));

        (await _flowState.TryCompleteAsync(StateOf(url), Xunit.TestContext.Current.CancellationToken))
            .Should().BeTrue("the attempt's lifetime is measured from the click, not from the page render");
    }

    [Fact]
    public async Task ProviderButton_FollowedInAFirstTabAfterASecondTabRendered_StillCompletes()
    {
        var firstTab = RenderUnderTest<Login>(_ => { });
        RenderUnderTest<Login>(_ => { });

        var url = await FollowGoogleButtonAsync(firstTab);

        (await _flowState.TryCompleteAsync(StateOf(url), Xunit.TestContext.Current.CancellationToken))
            .Should().BeTrue("the tab the user clicked in owns the device slot: the latest click wins");
    }

    [Fact]
    public async Task ProviderButton_ClickedWhenDeviceStorageDropsTheAttempt_StaysOnLoginAndExplains()
    {
        Services.AddSingleton(new OAuthFlowStateStore(new DroppingCacheStore(), _clock));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/login");
        var cut = RenderUnderTest<Login>(_ => { });

        await GoogleButton(cut).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        new Uri(navigation.Uri).AbsolutePath.Should().Be("/login", "a flow started without a kept attempt would be refused at completion");
        await cut.WaitForAssertionAsync(() => cut.Find("[role='alert']").TextContent
            .Should().Contain("External sign-in needs browser storage."));
    }

    // Follows the Google button the way a browser would: its href when it is a link, otherwise its
    // click handler, whose navigation the bUnit NavigationManager records.
    private async Task<string> FollowGoogleButtonAsync(IRenderedComponent<Login> cut)
    {
        var button = GoogleButton(cut);
        if (button.GetAttribute("href") is { } href)
        {
            return href;
        }

        await button.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        return Services.GetRequiredService<NavigationManager>().Uri;
    }

    private static AngleSharp.Dom.IElement GoogleButton(IRenderedComponent<Login> cut) =>
        cut.FindAll("a, button").Single(e => e.GetAttribute("aria-label") == "Sign in with Google");

    private static string? StateOf(string url)
    {
        var query = url.Contains('?', StringComparison.Ordinal) ? url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => pair.StartsWith("state=", StringComparison.Ordinal))
            .Select(pair => Uri.UnescapeDataString(pair["state=".Length..]))
            .FirstOrDefault();
    }

    private sealed class InMemoryCacheStore : ILocalCacheStore
    {
        private readonly Dictionary<string, string> _entries = [];

        public bool IsAvailable => true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _entries[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    // Reports itself available but keeps nothing (storage full, site data blocked, private mode).
    private sealed class DroppingCacheStore : ILocalCacheStore
    {
        public bool IsAvailable => true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
