using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.Shared.Http;
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

    [Fact]
    public async Task LoadAnswering404_SaysTheFeatureIsNotEnabled_InsteadOfTheFormListOrFailure()
    {
        // With machine clients off, every Admin/MachineClients action answers a bare NotFound(), which
        // [ApiController] turns into this Problem Details body; the UI service reads it back through
        // ProblemDetailsResultReader, so the component sees exactly this failure.
        const string notFoundBody = """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404}""";
        _clients.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<MachineClientResponse>>(
                ProblemDetailsResultReader.ParseProblemDetails(404, notFoundBody)));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        var localizer = Services.GetRequiredService<IStringLocalizer<MachineClientAdminResources>>();

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll("[data-testid=\"machine-clients-not-enabled\"]").Should().ContainSingle(
                "a 404 on the list means the server has machine clients turned off"));

        var notEnabled = localizer["NotEnabled"];
        notEnabled.ResourceNotFound.Should().BeFalse("the notice text comes from the NotEnabled resource key");
        cut.Find("[data-testid=\"machine-clients-not-enabled\"]").TextContent.Trim().Should().Contain(notEnabled.Value);
        cut.FindAll("[data-testid=\"create-client\"]").Should().BeEmpty("there is nothing to create a client against");
        cut.FindAll("[data-testid=\"client-row-name\"]").Should().BeEmpty();
        cut.FindAll(".mud-table").Should().BeEmpty("no client list is rendered");
        cut.FindAll("[role=\"alert\"]")
            .Where(alert => alert.Closest("[data-testid=\"machine-clients-not-enabled\"]") is null)
            .Should().BeEmpty("the generic load-failure summary is not shown");
        RetryButtons(cut, localizer).Should().BeEmpty("retrying cannot turn the feature on");
    }

    [Fact]
    public async Task LoadFailingOtherwise_ShowsTheGenericFailure_AndNoNotEnabledNotice()
    {
        const string message = "Machine client probe failure.";
        _clients.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<MachineClientResponse>>(
                Error.Failure("MachineClients.ProbeFailure", message)));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        var localizer = Services.GetRequiredService<IStringLocalizer<MachineClientAdminResources>>();

        await cut.WaitForAssertionAsync(() => RetryButtons(cut, localizer).Should().ContainSingle());

        cut.FindAll("[role=\"alert\"]").Should().Contain(
            alert => alert.TextContent.Contains(message, StringComparison.Ordinal),
            "any other failure keeps the generic inline summary");
        cut.FindAll("[data-testid=\"machine-clients-not-enabled\"]").Should().BeEmpty("only a 404 means the feature is off");
    }

    [Fact]
    public async Task Creating_SendsTheTypedNameAndRole_ShowsTheSecretOnce_AddsTheRow_AndClearsTheForm()
    {
        var created = new MachineClientResponse(Guid.NewGuid(), "Report export", 5003, DateTime.UtcNow, null, null, null, HasSecondarySecret: false);
        _clients.Setup(x => x.CreateAsync(It.IsAny<CreateMachineClientRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MachineClientSecretResponse(created, "new-secret")));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"client-row-name\"]").Should().HaveCount(2));

        await cut.Find("input[data-testid='client-name']").InputAsync(new ChangeEventArgs { Value = "Report export" });
        await cut.Find("input[data-testid='client-role']").InputAsync(new ChangeEventArgs { Value = "Reporter" });
        await cut.Find("[data-testid=\"create-client-submit\"]").ClickAsync(new MouseEventArgs());

        _clients.Verify(
            x => x.CreateAsync(new CreateMachineClientRequest("Report export", "Reporter"), It.IsAny<CancellationToken>()),
            Times.Once());
        await cut.WaitForAssertionAsync(() =>
            cut.Find("[data-testid=\"issued-client-secret\"]").TextContent.Should().Be("new-secret"));
        cut.FindAll("[data-testid=\"client-row-name\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal("Nightly sync", "Old exporter", "Report export");
        cut.Find("input[data-testid='client-name']").GetAttribute("value").Should().BeNullOrEmpty("a created client clears the form");
        cut.Find("input[data-testid='client-role']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid=\"create-client-submit\"]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Creating_WithNothingTyped_SendsEmptyValues_AndShowsTheRefusalInTheForm()
    {
        const string message = "Name is required.";
        _clients.Setup(x => x.CreateAsync(It.IsAny<CreateMachineClientRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<MachineClientSecretResponse>(Error.Validation("MachineClients.NameRequired", message)));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"client-row-name\"]").Should().HaveCount(2));

        await cut.Find("[data-testid=\"create-client-submit\"]").ClickAsync(new MouseEventArgs());

        _clients.Verify(
            x => x.CreateAsync(new CreateMachineClientRequest(string.Empty, string.Empty), It.IsAny<CancellationToken>()),
            Times.Once());
        await cut.WaitForAssertionAsync(() =>
            cut.Find("[data-testid=\"create-client\"]").TextContent.Should().Contain(message));
        cut.FindAll("[data-testid=\"issued-secret\"]").Should().BeEmpty("a refused create issues no secret");
        cut.FindAll("[data-testid=\"client-row-name\"]").Should().HaveCount(2);
        cut.Find("[data-testid=\"create-client-submit\"]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Creating_AfterARefusal_ClearsTheRefusalOnSuccess_AndKeepsNoTypedValues()
    {
        const string message = "Role is unknown.";
        var created = new MachineClientResponse(Guid.NewGuid(), "Report export", 5003, DateTime.UtcNow, null, null, null, HasSecondarySecret: false);
        _clients.SetupSequence(x => x.CreateAsync(It.IsAny<CreateMachineClientRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<MachineClientSecretResponse>(Error.Validation("MachineClients.RoleUnknown", message)))
            .ReturnsAsync(Result.Success(new MachineClientSecretResponse(created, "second-try")));
        var cut = RenderUnderTest<MachineClientAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"client-row-name\"]").Should().HaveCount(2));
        await cut.Find("input[data-testid='client-name']").InputAsync(new ChangeEventArgs { Value = "Report export" });
        await cut.Find("input[data-testid='client-role']").InputAsync(new ChangeEventArgs { Value = "Nope" });
        await cut.Find("[data-testid=\"create-client-submit\"]").ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=\"create-client\"]").TextContent.Should().Contain(message));
        cut.Find("input[data-testid='client-name']").GetAttribute("value").Should().Be("Report export", "a refusal keeps what was typed");

        await cut.Find("[data-testid=\"create-client-submit\"]").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[data-testid=\"issued-client-secret\"]").TextContent.Should().Be("second-try"));
        cut.Find("[data-testid=\"create-client\"]").TextContent.Should().NotContain(message);
    }

    /// <summary>The component's own retry buttons, found by their localized label.</summary>
    private static IEnumerable<IElement> RetryButtons(
        IRenderedComponent<MachineClientAdmin> cut,
        IStringLocalizer<MachineClientAdminResources> localizer) =>
        cut.FindAll("button").Where(button =>
            string.Equals(button.TextContent.Trim(), localizer["Action.Retry"].Value, StringComparison.Ordinal));
}
