using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Guard for the Profile half of <see cref="FormsConventionTestsBase"/>: the password-form marker list
/// is what both the consumers' inline Profile forms and the shared <c>ChangePasswordCard</c> are held
/// to, so it must flag every missing piece and pass a complete form. MMCA.Common has no
/// <c>Source/Modules</c> tree, so the create-form facts run in the consumers only.
/// </summary>
public sealed class FormsConventionTestsBaseTests
{
    private const string CompleteForm =
        "<MudForm @ref=\"_passwordForm\">"
        + "<MudTextField Required=\"true\" RequiredError=\"a\" />"
        + "<MudTextField Required=\"true\" RequiredError=\"b\" Validation=\"@(new Func<string, string?>(ValidateNewPassword))\" />"
        + "<MudTextField Required=\"true\" RequiredError=\"c\" Validation=\"@(new Func<string, string?>(ValidateConfirmPassword))\" />"
        + "</MudForm>"
        + "<ErrorSummary Messages=\"_passwordForm?.Errors\" />";

    [Fact]
    public void CompletePasswordForm_HasNoMissingMarkers() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(CompleteForm).Should().BeEmpty();

    [Fact]
    public void PasswordForm_WithoutErrorSummary_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("<ErrorSummary Messages=\"_passwordForm?.Errors\" />", string.Empty, StringComparison.Ordinal))
            .Should().Contain("<ErrorSummary").And.Contain("Messages=\"_passwordForm?.Errors\"");

    [Fact]
    public void PasswordForm_WithAFieldNoLongerRequired_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("Required=\"true\" RequiredError=\"a\" ", string.Empty, StringComparison.Ordinal))
            .Should().HaveCount(2, "both the requiredness and the required message dropped below three fields");

    [Fact]
    public void PasswordForm_WithoutMatchValidation_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("ValidateConfirmPassword", "Other", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be("ValidateConfirmPassword");
}
