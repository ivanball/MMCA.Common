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
/// D9 (Run 15): the card's ErrorSummary reads <c>MudForm.Errors</c>, but the form re-validating a
/// field does not re-render the card, so after a wrong current password the summary kept "Current
/// password is incorrect." once the user edited the field and its own message had cleared. The card
/// now re-renders when the form's errors change, so the summary follows the field.
/// </summary>
public sealed class ChangePasswordCardErrorSummaryTests : BunitTestBase
{
    private const string Refusal = "The current password is incorrect.";

    private readonly Mock<IAuthUIService> _auth = new();

    public ChangePasswordCardErrorSummaryTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(Mock.Of<IToastService>());
    }

    [Fact]
    public void WrongCurrentPassword_ThenEditingTheField_ClearsTheSummary()
    {
        _auth.Setup(a => a.ChangePasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation("Auth.InvalidCurrentPassword", Refusal)));
        var cut = RenderUnderTest<ChangePasswordCard>(_ => { });

        Fill(cut, "Wrong-1", "New-Password-1", "New-Password-1");
        cut.Find(".mud-card-actions button").Click();
        cut.WaitForAssertion(() => Summary(cut).Should().Contain(Refusal));

        cut.FindAll("input[type=password]")[0].Change("Right-1");

        cut.WaitForAssertion(() => Summary(cut).Should().NotContain(Refusal));
    }

    // The ErrorSummary is the only role="alert" block the card renders; empty when it rendered nothing.
    private static string Summary(IRenderedComponent<ChangePasswordCard> cut) =>
        string.Concat(cut.FindAll("[role=alert]").Select(alert => alert.TextContent));

    private static void Fill(IRenderedComponent<ChangePasswordCard> cut, string current, string next, string confirm)
    {
        cut.FindAll("input[type=password]")[0].Change(current);
        cut.FindAll("input[type=password]")[1].Change(next);
        cut.FindAll("input[type=password]")[2].Change(confirm);
    }
}
