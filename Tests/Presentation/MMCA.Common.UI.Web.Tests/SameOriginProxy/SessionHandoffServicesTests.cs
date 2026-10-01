using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using MMCA.Common.UI.Web.SameOriginProxy;
using Moq;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The Blazor Server circuit's side of the handoff: the refresher opens the ciphertext the page fetched
/// (and treats a forged or missing one as "no session"), and the cookie sync hands script only
/// ciphertext, never the token pair it is asked to store.
/// </summary>
public sealed class SessionHandoffServicesTests
{
    private readonly SessionHandoffProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly Mock<IJSRuntime> _js = new();

    [Fact]
    public async Task Refresher_OpensTheHandoffServerSide()
    {
        _js.Setup(j => j.InvokeAsync<string?>("mmcaAuthHandoff.getToken", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(_protector.ProtectAccessToken("access-1"));

        var token = await new HandoffTokenRefresher(_js.Object, _protector).AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        token.Should().Be("access-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged")]
    public async Task Refresher_AMissingOrForgedHandoff_IsNoSession(string? handoff)
    {
        _js.Setup(j => j.InvokeAsync<string?>("mmcaAuthHandoff.getToken", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(handoff);

        var token = await new HandoffTokenRefresher(_js.Object, _protector).AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        token.Should().BeNull();
    }

    [Fact]
    public async Task Refresher_InteropUnavailable_IsNoSession()
    {
        _js.Setup(j => j.InvokeAsync<string?>("mmcaAuthHandoff.getToken", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new JSDisconnectedException("gone"));

        var token = await new HandoffTokenRefresher(_js.Object, _protector).AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        token.Should().BeNull();
    }

    [Fact]
    public async Task CookieSync_HandsScriptCiphertextOnly()
    {
        object?[]? sent = null;
        _js.Setup(j => j.InvokeAsync<bool>("mmcaAuthHandoff.setCookie", It.IsAny<object?[]?>()))
            .Callback<string, object?[]?>((_, args) => sent = args)
            .ReturnsAsync(true);

        var written = await new HandoffSessionCookieSync(_js.Object, _protector).SyncAsync("access-1", "refresh-1");

        written.Should().BeTrue();
        var handoff = sent.Should().ContainSingle().Subject.Should().BeOfType<string>().Subject;
        handoff.Should().NotContain("access-1").And.NotContain("refresh-1");
        _protector.UnprotectTokenPair(handoff).Should().Be(("access-1", "refresh-1"));
    }

    [Fact]
    public async Task CookieSync_ClearUsesTheExistingCookieDelete()
    {
        _js.Setup(j => j.InvokeAsync<bool>("mmcaAuthCookie.clear", It.IsAny<object?[]?>())).ReturnsAsync(true);

        var cleared = await new HandoffSessionCookieSync(_js.Object, _protector).ClearAsync();

        cleared.Should().BeTrue();
    }
}
