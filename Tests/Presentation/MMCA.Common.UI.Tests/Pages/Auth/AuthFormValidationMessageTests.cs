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

    // Reset Password and Forgot Password have one required field (Email); Login and Register two
    // (Email and Password). Each form is checked on an empty submit and with every field touched.
    [Theory]
    [InlineData(nameof(ResetPassword), nameof(Submit))]
    [InlineData(nameof(ResetPassword), nameof(TouchEveryField))]
    [InlineData(nameof(ForgotPassword), nameof(Submit))]
    [InlineData(nameof(ForgotPassword), nameof(TouchEveryField))]
    [InlineData(nameof(Login), nameof(Submit))]
    [InlineData(nameof(Login), nameof(TouchEveryField))]
    [InlineData(nameof(Register), nameof(Submit))]
    [InlineData(nameof(Register), nameof(TouchEveryField))]
    public void AuthForm_EmptyFields_ShowLocalizedMessagesNotResourceKeys(string form, string trigger)
    {
        switch (form)
        {
            case nameof(ResetPassword):
                AssertLocalizedMessages<ResetPassword>(trigger, "Email is required");
                break;
            case nameof(ForgotPassword):
                AssertLocalizedMessages<ForgotPassword>(trigger, "Email is required");
                break;
            case nameof(Login):
                AssertLocalizedMessages<Login>(trigger, "Email is required", "Password is required");
                break;
            case nameof(Register):
                AssertLocalizedMessages<Register>(trigger, "Email is required", "Password is required");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form.");
        }
    }

    // Run 19 N1: type=email makes the browser refuse an internationalized local part before the form
    // submits, on web Chrome and in the MAUI WebView. Each form's email field is a text input that
    // still asks for the email keyboard and autofill.
    [Theory]
    [InlineData(nameof(ResetPassword))]
    [InlineData(nameof(ForgotPassword))]
    [InlineData(nameof(Login))]
    [InlineData(nameof(Register))]
    public void AuthForm_EmailField_IsATextInputWithTheEmailKeyboard(string form)
    {
        var input = form switch
        {
            nameof(ResetPassword) => EmailInputOf<ResetPassword>(),
            nameof(ForgotPassword) => EmailInputOf<ForgotPassword>(),
            nameof(Login) => EmailInputOf<Login>(),
            nameof(Register) => EmailInputOf<Register>(),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form."),
        };

        input.GetAttribute("type").Should().Be("text");
        input.GetAttribute("inputmode").Should().Be("email");
        input.GetAttribute("autocomplete").Should().StartWith("email");
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

    private void AssertLocalizedMessages<TComponent>(string trigger, params string[] expectedMessages)
        where TComponent : IComponent
    {
        var cut = RenderUnderTest<TComponent>(_ => { });

        if (trigger == nameof(Submit))
        {
            Submit(cut);
        }
        else
        {
            TouchEveryField(cut);
        }

        cut.WaitForAssertion(() =>
        {
            foreach (var message in expectedMessages)
            {
                cut.Markup.Should().Contain(message);
            }
        });
        cut.Markup.Should().NotContain(RawKeyPrefix);
    }

    private AngleSharp.Dom.IElement EmailInputOf<TComponent>()
        where TComponent : IComponent =>
        RenderUnderTest<TComponent>(_ => { }).Find("form input[autocomplete^='email']");

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
