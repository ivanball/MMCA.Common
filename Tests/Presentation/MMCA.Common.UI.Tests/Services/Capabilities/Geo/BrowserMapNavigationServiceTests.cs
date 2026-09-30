using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities.Geo;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Geo;

/// <summary>
/// Verifies <see cref="BrowserMapNavigationService"/> reports what the link opener reported (L70):
/// the contract is "returns whether a maps UI was opened", and callers branch on it to offer a
/// fallback, so an unconditional true hid every failure.
/// </summary>
public sealed class BrowserMapNavigationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAddressAsync_ReturnsWhetherTheLinkOpened(bool opened)
    {
        var links = new Mock<IExternalLinkService>();
        links.Setup(l => l.OpenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync(opened);
        var sut = new BrowserMapNavigationService(links.Object);

        var result = await sut.OpenAddressAsync("1 Main St, Atlanta", label: null, TestContext.Current.CancellationToken);

        result.Should().Be(opened);
        links.Verify(
            l => l.OpenAsync(
                It.Is<Uri>(u => u.AbsoluteUri.StartsWith("https://www.google.com/maps/search/?api=1&query=", StringComparison.Ordinal)
                    && u.AbsoluteUri.Contains("Atlanta", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }
}
