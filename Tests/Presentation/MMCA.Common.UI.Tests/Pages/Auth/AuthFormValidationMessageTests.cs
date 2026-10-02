using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Preferences;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// The four anonymous auth forms (Login, Register, Forgot Password, Reset Password) must show the
/// LOCALIZED message for an empty required field, never the raw resource key such as
/// <c>Auth.Field.Email.Required</c>. Two validation passes reach the screen: the form's own (on
/// submit) and each field's own, which MudBlazor runs from the <c>For</c> expression's DataAnnotations
/// attributes when a field is touched (tabbed through or clicked away from). Both are exercised here.
/// </summary>
public sealed class AuthFormValidationMessageTests : BunitTestBase
{
    private const string RawKeyPrefix = "Auth.Field.";

    public AuthFormValidationMessageTests()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton(new OAuthFlowStateStore(new Mock<ILocalCacheStore>().Object));
        Services.AddSingleton(new Mock<IOAuthUISettings>().Object);
        Services.AddSingleton(Options.Create(new ApiSettings()));
        Services.AddSingleton(new Mock<IUserPreferenceReader>().Object);
    }

    // ==================== Reset Password ====================
    [Fact]
    public void ResetPassword_EmptySubmit_ShowsLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<ResetPassword>(_ => { });

        Submit(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Email is required"));
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    [Fact]
    public void ResetPassword_TouchedEmptyFields_ShowLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<ResetPassword>(_ => { });

        TouchEveryField(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Email is required"));
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    [Fact]
    public void ResetPassword_TouchedEmptyFields_InSpanish_ShowTheSpanishMessage()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
        try
        {
            var cut = RenderUnderTest<ResetPassword>(_ => { });

            TouchEveryField(cut);

            cut.WaitForAssertion(() => cut.Markup.Should().Contain("El correo electrónico es obligatorio"));
            cut.Markup.Should().NotContain(RawKeyPrefix);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ==================== Forgot Password ====================
    [Fact]
    public void ForgotPassword_EmptySubmit_ShowsLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<ForgotPassword>(_ => { });

        Submit(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Email is required"));
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    [Fact]
    public void ForgotPassword_TouchedEmptyFields_ShowLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<ForgotPassword>(_ => { });

        TouchEveryField(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Email is required"));
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    // ==================== Login ====================
    [Fact]
    public void Login_EmptySubmit_ShowsLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        Submit(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Email is required");
            cut.Markup.Should().Contain("Password is required");
        });
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    [Fact]
    public void Login_TouchedEmptyFields_ShowLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<Login>(_ => { });

        TouchEveryField(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Email is required");
            cut.Markup.Should().Contain("Password is required");
        });
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    // ==================== Register ====================
    [Fact]
    public void Register_EmptySubmit_ShowsLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<Register>(_ => { });

        Submit(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Email is required");
            cut.Markup.Should().Contain("Password is required");
        });
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    [Fact]
    public void Register_TouchedEmptyFields_ShowLocalizedMessagesNotResourceKeys()
    {
        var cut = RenderUnderTest<Register>(_ => { });

        TouchEveryField(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Email is required");
            cut.Markup.Should().Contain("Password is required");
        });
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    private static void Submit<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.Find("form button[type='submit']").Click();

    /// <summary>
    /// Tabs through every text field without typing: each one loses focus untouched, which is what
    /// runs MudBlazor's own field-level validation.
    /// </summary>
    private static void TouchEveryField<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        foreach (var input in cut.FindAll("form input.mud-input-slot"))
        {
            input.Blur();
        }
    }
}
