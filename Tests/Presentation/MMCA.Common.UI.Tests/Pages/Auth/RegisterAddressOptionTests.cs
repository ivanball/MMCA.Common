using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// The optional address block on the Register page is a host choice (<see cref="RegistrationSettings.CollectAddress"/>,
/// bound from the <c>"Registration"</c> section like <see cref="LegalSettings"/>): on by default, so a
/// host that configures nothing keeps the page it had; off, the page offers no address fields and the
/// request carries no address (a host whose user model stores no address must not ask for one).
/// </summary>
public sealed class RegisterAddressOptionTests : BunitTestBase
{
    private const string AddressLine1Selector = "input[autocomplete='address-line1']";
    private const string CitySelector = "input[autocomplete='address-level2']";

    private readonly Mock<IAuthUIService> _auth = new();

    public RegisterAddressOptionTests()
    {
        Services.AddSingleton(_auth.Object);
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new AuthenticationResponse(
                "access-token",
                "refresh-token",
                new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc))));
    }

    // ── Default: unchanged page (regression guard; passes before and after the fix) ──
    [Fact]
    public void ByDefault_RendersTheAddressBlock()
    {
        UseRegistration(new RegistrationSettings());

        var cut = RenderUnderTest<Register>(_ => { });

        cut.FindAll(AddressLine1Selector).Should().ContainSingle("collecting the address is the default");
        cut.Markup.Should().Contain("Address (Optional)");
    }

    // ── CollectAddress = false ──
    [Fact]
    public void WhenAddressCollectionIsOff_RendersNoAddressBlock()
    {
        UseRegistration(new RegistrationSettings { CollectAddress = false });

        var cut = RenderUnderTest<Register>(_ => { });

        cut.FindAll(AddressLine1Selector).Should().BeEmpty("the host turned address collection off");
        cut.FindAll(CitySelector).Should().BeEmpty();
        cut.FindAll(".mud-expand-panel").Should().BeEmpty("no address panel is offered at all");
        cut.Markup.Should().NotContain("Address (Optional)");
    }

    [Fact]
    public void WhenAddressCollectionIsOff_SubmittingSendsNoAddress()
    {
        // Guard for the submit half: with the block gone the request must still go out, address-less.
        UseRegistration(new RegistrationSettings { CollectAddress = false });
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => _auth.Verify(
            x => x.RegisterAsync(It.Is<RegisterRequest>(r => r.Address == null), It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    // ── Binding: the "Registration" section reaches IOptions<RegistrationSettings> via AddUIShared ──
    [Fact]
    public void AddUIShared_BindsTheRegistrationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:ApiEndpoint"] = "https://localhost:6001",
                ["Registration:CollectAddress"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMudServices();
        services.AddUIShared(configuration);

        using var provider = services.BuildServiceProvider();

        RegistrationSettings.SectionName.Should().Be("Registration");
        provider.GetRequiredService<IOptions<RegistrationSettings>>().Value.CollectAddress.Should().BeFalse(
            "AddUIShared binds the Registration section the same way it binds Legal");
    }

    private static void FillRequiredFields(IRenderedComponent<Register> cut)
    {
        cut.Find("input[autocomplete='given-name']").Input("Ada");
        cut.Find("input[autocomplete='family-name']").Input("Lovelace");
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.FindAll("input[autocomplete='new-password']")[0].Input("Str0ng!Passw0rd");
        cut.FindAll("input[autocomplete='new-password']")[1].Input("Str0ng!Passw0rd");
    }

    private void UseRegistration(RegistrationSettings settings) =>
        Services.AddSingleton<IOptions<RegistrationSettings>>(Options.Create(settings));
}
