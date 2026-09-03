using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// bUnit tests for the Register EditForm (rubric §24): submitting an empty form shows field-level
/// validation messages tied to each input and does not call the auth service, an invalid optional
/// address blocks the submit, and a failed registration keeps the user on the page with the server's
/// own wording in the form-level alert.
/// </summary>
public sealed class RegisterFormTests : BunitTestBase
{
    private readonly Mock<IAuthUIService> _auth = new();

    public RegisterFormTests() => Services.AddSingleton(_auth.Object);

    private static Result<AuthenticationResponse> Registered()
        => Result.Success(new AuthenticationResponse(
            "access-token",
            "refresh-token",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));

    private void RegistrationReturns(Result<AuthenticationResponse> result) =>
        _auth
            .Setup(x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public void SubmittingEmptyForm_ShowsFieldLevelValidation_AndDoesNotRegister()
    {
        var cut = RenderUnderTest<Register>(_ => { });

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("First name is required");
            cut.Markup.Should().Contain("Email is required");
            cut.Markup.Should().Contain("Password is required");
        });
        _auth.Verify(
            x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void SubmittingWithAnInvalidAddress_ShowsTheAddressError_AndDoesNotRegister()
    {
        // The optional address block used to be built with Address.Create and its failure discarded,
        // so a partially-filled address silently created an account with NO address at all. Anything
        // the user typed is now validated, and the failure blocks the submit.
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        // City entered, address line 1 left blank: an address the value object refuses to build.
        // The address fields are not Immediate, so they commit on change rather than on input.
        cut.Find("input[autocomplete='address-level2']").Change("Atlanta");

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Address Line 1 cannot be empty"));
        _auth.Verify(
            x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public void SubmittingWithNoAddressAtAll_StillRegisters()
    {
        // The address block is optional: an untouched block must still register, with a null address.
        RegistrationReturns(Registered());
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => _auth.Verify(
            x => x.RegisterAsync(It.Is<RegisterRequest>(r => r.Address == null), It.IsAny<CancellationToken>()),
            Times.Once()));
    }

    [Fact]
    public void WhenRegistrationSucceeds_LeavesNoErrorOnScreen()
    {
        RegistrationReturns(Registered());
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => _auth.Verify(
            x => x.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()),
            Times.Once()));
        cut.FindAll(".mud-alert").Should().BeEmpty("a successful registration has nothing to report");
    }

    [Fact]
    public void WhenRegistrationFails_ShowsTheServersWordingInTheFormAlert()
    {
        // The failure's own message replaces the removed LastError property: whatever the API said is
        // what the user reads, and the user stays on the form with the typed values intact.
        RegistrationReturns(Result.Failure<AuthenticationResponse>(
            Error.Conflict("Auth.Register.EmailTaken", "That email address is already registered.")));
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("That email address is already registered."));
        cut.FindAll("input[autocomplete='email']").Should().ContainSingle(
            "a failed registration leaves the user on the form rather than navigating away");
    }

    [Fact]
    public void WhenTheFailureMessageIsAResourceKey_ItIsLocalizedBeforeItIsShown()
    {
        // Messages travel as resource keys with pass-through (ADR-027), so a client-side or server
        // key resolves to the active culture's wording instead of leaking the key itself.
        RegistrationReturns(Result.Failure<AuthenticationResponse>(
            Error.Failure("Auth.Register.Failed", "Auth.Register.Failed")));
        var cut = RenderUnderTest<Register>(_ => { });
        FillRequiredFields(cut);

        cut.ClickButtonByText("Create Account");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registration failed. Please try again."));
        cut.Markup.Should().NotContain("Auth.Register.Failed");
    }

    private static void FillRequiredFields(IRenderedComponent<Register> cut)
    {
        cut.Find("input[autocomplete='given-name']").Input("Ada");
        cut.Find("input[autocomplete='family-name']").Input("Lovelace");
        cut.Find("input[autocomplete='email']").Input("ada@example.com");
        cut.FindAll("input[autocomplete='new-password']")[0].Input("Str0ng!Passw0rd");
        cut.FindAll("input[autocomplete='new-password']")[1].Input("Str0ng!Passw0rd");
    }
}
