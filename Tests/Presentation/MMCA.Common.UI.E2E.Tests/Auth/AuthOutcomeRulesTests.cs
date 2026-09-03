using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The four-way classification behind <c>E2ETestBase.WaitForAuthResultAsync</c>. Browser-free: the
/// decision is a pure function precisely so the silent case can be pinned without staging a server
/// that fails without rendering anything.
/// </summary>
public sealed class AuthOutcomeRulesTests
{
    [Fact]
    public void AllSignalsQuiet_IsSilent() =>
        AuthOutcomeRules.Classify(navigatedAway: false, errorAlertVisible: false, logoutVisible: false)
            .Should().Be(
                AuthOutcome.Silent,
                "a submit that produced no navigation, no signed-in state and no error alert was never accepted");

    [Fact]
    public void NavigatedAway_WinsOverAnErrorAlert() =>
        AuthOutcomeRules.Classify(navigatedAway: true, errorAlertVisible: true, logoutVisible: false)
            .Should().Be(
                AuthOutcome.Succeeded,
                "an alert flashed on the way out of a completed forceLoad is not a failure");

    [Fact]
    public void LogoutVisibleAlone_IsSucceeded() =>
        AuthOutcomeRules.Classify(navigatedAway: false, errorAlertVisible: false, logoutVisible: true)
            .Should().Be(AuthOutcome.Succeeded);

    [Fact]
    public void LogoutVisible_WinsOverAnErrorAlert() =>
        AuthOutcomeRules.Classify(navigatedAway: false, errorAlertVisible: true, logoutVisible: true)
            .Should().Be(AuthOutcome.Succeeded);

    [Fact]
    public void ErrorAlertAlone_IsErrorShown() =>
        AuthOutcomeRules.Classify(navigatedAway: false, errorAlertVisible: true, logoutVisible: false)
            .Should().Be(AuthOutcome.ErrorShown);

    [Fact]
    public void NavigatedAwayAlone_IsSucceeded() =>
        AuthOutcomeRules.Classify(navigatedAway: true, errorAlertVisible: false, logoutVisible: false)
            .Should().Be(AuthOutcome.Succeeded);
}
