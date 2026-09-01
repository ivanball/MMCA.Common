namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>What the three post-submit signals add up to.</summary>
internal enum AuthOutcome
{
    /// <summary>The submit was accepted: the page navigated away, or a signed-in state is showing.</summary>
    Succeeded,

    /// <summary>The auth page is still showing, with an error alert on it.</summary>
    ErrorShown,

    /// <summary>None of the three signals fired. Neither success nor a reported failure.</summary>
    Silent,
}

/// <summary>
/// The decision behind <c>E2ETestBase.WaitForAuthResultAsync</c>, pulled out as a pure function so
/// the four-way classification is directly testable without a browser.
/// <para>
/// The case that matters is <see cref="AuthOutcome.Silent"/>. A submit that fails with NEITHER a
/// navigation NOR a rendered error alert (a 500 that renders nothing, a dropped request, a JS
/// exception mid-submit) used to leave the wait returning normally, and the caller's follow-up
/// interactivity wait was already satisfied by the still-rendered auth page. Login and registration
/// then reported success on a sign-in that never happened, and the real failure surfaced much later
/// as an unrelated assertion.
/// </para>
/// </summary>
internal static class AuthOutcomeRules
{
    /// <summary>
    /// Classifies the three signals. Navigation away from the auth page wins outright: a forceLoad
    /// that already happened is unambiguous, and an error alert flashed on the way out is not a
    /// failure. A visible logout button is the same verdict reached through interactivity instead.
    /// </summary>
    /// <param name="navigatedAway">The URL no longer points at the auth page.</param>
    /// <param name="errorAlertVisible">An error alert is showing.</param>
    /// <param name="logoutVisible">The signed-in state's logout control is showing.</param>
    public static AuthOutcome Classify(bool navigatedAway, bool errorAlertVisible, bool logoutVisible)
    {
        if (navigatedAway || logoutVisible)
        {
            return AuthOutcome.Succeeded;
        }

        return errorAlertVisible ? AuthOutcome.ErrorShown : AuthOutcome.Silent;
    }
}
