using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Devices;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// The signed-in devices page names a native MMCA app session "{AppName} app on {Platform}" instead of
/// "Unrecognized device" (the MAUI app now sends an <see cref="AppUserAgent"/> header), while a browser
/// session on the same page keeps its "{Browser} on {Platform}" label.
/// </summary>
public sealed class SessionsAppDeviceLabelTests : BunitTestBase
{
    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const string AtlDevConOnAndroid = "AtlDevCon/1.9.2 (Android 15; MmcaApp)";

    private static readonly DateTime CreatedAt = new(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = new(2026, 9, 1, 9, 30, 0, DateTimeKind.Utc);

    private readonly Mock<IAuthUIService> _auth = new();

    public SessionsAppDeviceLabelTests()
    {
        Services.AddSingleton(_auth.Object);
        _auth.Setup(a => a.GetSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>(
            [
                new(Guid.NewGuid(), CreatedAt, ExpiresAt, "203.0.113.7", ChromeOnWindows, IsCurrent: true),
                new(Guid.NewGuid(), CreatedAt, ExpiresAt, "198.51.100.4", AtlDevConOnAndroid, IsCurrent: false),
            ]));
    }

    [Fact]
    public void AnAppSession_IsLabelledAsTheAppOnItsPlatform()
    {
        var cut = RenderAs<Sessions>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain("AtlDevCon app on Android");
        cut.Markup.Should().NotContain("Unrecognized device");
    }

    [Fact]
    public void AnAppSession_NamesTheAppInItsSignOutButtonToo()
    {
        var cut = RenderAs<Sessions>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.WaitForAssertion(() =>
            cut.FindAll("button[aria-label=\"Sign out of AtlDevCon app on Android\"]").Should().ContainSingle());
        cut.Markup.Should().Contain("Chrome on Windows");
    }
}
