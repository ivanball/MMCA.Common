using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// The shared change-password section both consumers' profile pages render: client-side validation
/// (required, min-length, match) stops an invalid submit before any server call, a valid submit calls
/// <see cref="IAuthUIService.ChangePasswordAsync"/>, and the outcome is toasted in the component's own
/// localized text and reported through <c>OnChanged</c> / <c>OnFailed</c>.
/// </summary>
public sealed class ChangePasswordCardTests : BunitTestBase
{
    private readonly Mock<IAuthUIService> _auth = new();
    private readonly Mock<IToastService> _toast = new();

    public ChangePasswordCardTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(_toast.Object);
    }

    [Fact]
    public void Renders_ThreePasswordFields_AndTheHelperTextForTheMinimum()
    {
        var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

        cut.FindAll("input[type=password]").Should().HaveCount(3);
        cut.Markup.Should().Contain("Minimum 8 characters");
        SubmitButton(cut).TextContent.Should().Contain("Change Password");
    }

    [Fact]
    public void MinLengthParameter_DrivesTheHelperText()
    {
        var cut = RenderUnderTest<ChangePasswordCard>(p => p.Add(c => c.MinLength, 12));

        cut.Markup.Should().Contain("Minimum 12 characters");
    }

    [Fact]
    public void EmptySubmit_ShowsTheRequiredMessages_AndCallsNoService()
    {
        var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

        SubmitButton(cut).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Current password is required."));
        _auth.Verify(a => a.ChangePasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ShortNewPassword_IsRejectedClientSide()
    {
        var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

        Fill(cut, "Current-1", "short", "short");
        SubmitButton(cut).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("New password must be at least 8 characters."));
        _auth.Verify(a => a.ChangePasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MismatchedConfirmation_IsRejectedClientSide()
    {
        var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

        Fill(cut, "Current-1", "New-Password-1", "New-Password-2");
        SubmitButton(cut).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("New passwords do not match."));
        _auth.Verify(a => a.ChangePasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ValidSubmit_ChangesThePassword_ToastsSuccess_AndRaisesOnChanged()
    {
        _auth.Setup(a => a.ChangePasswordAsync("Current-1", "New-Password-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var changed = false;
        var cut = RenderUnderTest<ChangePasswordCard>(p => p.Add(c => c.OnChanged, () => changed = true));

        Fill(cut, "Current-1", "New-Password-1", "New-Password-1");
        SubmitButton(cut).Click();

        cut.WaitForAssertion(() => changed.Should().BeTrue());
        _toast.Verify(t => t.Success("Password changed successfully."), Times.Once);
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RefusedChange_ToastsFailure_AndRaisesOnFailedWithTheResult()
    {
        var refusal = Result.Failure(Error.Validation("Auth.InvalidCurrentPassword", "The current password is incorrect."));
        _auth.Setup(a => a.ChangePasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refusal);
        Result? reported = null;
        var cut = RenderUnderTest<ChangePasswordCard>(p => p.Add(c => c.OnFailed, r => reported = r));

        Fill(cut, "Wrong-1", "New-Password-1", "New-Password-1");
        SubmitButton(cut).Click();

        cut.WaitForAssertion(() => reported.Should().BeSameAs(refusal));
        _toast.Verify(t => t.Error("Failed to change password. Check your current password."), Times.Once);
        _toast.Verify(t => t.Success(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void InSpanish_RendersTheSpanishLabels()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
        try
        {
            var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

            cut.Markup.Should().Contain("Contraseña actual");
            cut.Markup.Should().Contain("Mínimo 8 caracteres");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static AngleSharp.Dom.IElement SubmitButton(IRenderedComponent<ChangePasswordCard> cut) =>
        cut.Find(".mud-card-actions button");

    private static void Fill(IRenderedComponent<ChangePasswordCard> cut, string current, string next, string confirm)
    {
        var inputs = cut.FindAll("input[type=password]");
        inputs[0].Change(current);
        inputs = cut.FindAll("input[type=password]");
        inputs[1].Change(next);
        inputs = cut.FindAll("input[type=password]");
        inputs[2].Change(confirm);
    }
}
