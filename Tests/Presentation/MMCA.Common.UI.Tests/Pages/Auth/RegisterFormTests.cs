using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// bUnit tests for the Register EditForm (rubric §24): submitting an empty form shows field-level
/// validation messages tied to each input and does not call the auth service.
/// </summary>
public sealed class RegisterFormTests : BunitTestBase
{
    private readonly Mock<IAuthUIService> _auth = new();

    public RegisterFormTests() => Services.AddSingleton(_auth.Object);

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
}
