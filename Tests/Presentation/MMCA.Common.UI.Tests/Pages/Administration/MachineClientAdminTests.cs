using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="MachineClientAdmin"/> component (ADR-140): the clients are
/// listed with their status, a revoked client offers no action, and a secret the API issues is shown
/// once until dismissed.
/// </summary>
public sealed class MachineClientAdminTests : BunitTestBase
{
    private static readonly MachineClientResponse Active =
        new(Guid.NewGuid(), "Nightly sync", 5001, DateTime.UtcNow, null, null, null, HasSecondarySecret: false);

    private static readonly MachineClientResponse Revoked =
        new(Guid.NewGuid(), "Old exporter", 5002, DateTime.UtcNow, null, DateTime.UtcNow, null, HasSecondarySecret: false);

    private readonly Mock<IMachineClientAdminUIService> _clients = new();

    public MachineClientAdminTests()
    {
        Services.AddSingleton(_clients.Object);
        _clients.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<MachineClientResponse>>([Active, Revoked]));
    }

    [Fact]
    public async Task ListsTheClients_AndARevokedOneOffersNoAction()
    {
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });

        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"client-row-name\"]").Should().HaveCount(2));
        cut.FindAll("[data-testid=\"client-row-name\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal("Nightly sync", "Old exporter");
        cut.FindAll("[data-testid=\"revoke-client\"]").Should().ContainSingle("only the active client can be revoked");
        cut.FindAll("[data-testid=\"issued-secret\"]").Should().BeEmpty("no secret is shown until one is issued");
    }

    [Fact]
    public async Task Rotating_ShowsTheNewSecretOnce_UntilDismissed()
    {
        var rotated = Active with { HasSecondarySecret = true };
        _clients.Setup(x => x.RotateAsync(Active.ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MachineClientSecretResponse(rotated, "s3cr3t-value")));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"rotate-client\"]").Should().ContainSingle());

        await cut.Find("[data-testid=\"rotate-client\"]").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[data-testid=\"issued-client-secret\"]").TextContent.Should().Be("s3cr3t-value"));
        cut.FindAll("[data-testid=\"promote-client\"]").Should().ContainSingle("a waiting secret can now be promoted");

        await cut.Find("[data-testid=\"dismiss-secret\"]").ClickAsync(new MouseEventArgs());

        cut.FindAll("[data-testid=\"issued-secret\"]").Should().BeEmpty();
        cut.Markup.Should().NotContain("s3cr3t-value", "a dismissed secret is gone from the page for good");
    }
}
