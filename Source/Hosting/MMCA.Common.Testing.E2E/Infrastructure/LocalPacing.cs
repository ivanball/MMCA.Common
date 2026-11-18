using System.Globalization;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Optional pauses between the phases of the longest workflows. An interactively started AppHost keeps
/// the Gateway's per-client-IP limits (the edge window of 120 requests a minute, and the tighter
/// sign-in policy), and every request a local run makes, the UI host's server side included, arrives
/// from loopback, so a workflow that arranges data, walks a user through it and comes back as an
/// administrator can run past them and read "Too many requests" or a page's load-failed state. CI
/// lifts those limits and leaves <c>E2E_LOCAL_PACING_SECONDS</c> unset, so the pauses cost nothing
/// there.
/// </summary>
public static class LocalPacing
{
    /// <summary>The environment variable naming the pause, in whole seconds.</summary>
    public const string SecondsVariable = "E2E_LOCAL_PACING_SECONDS";

    /// <summary>Gets the configured pause: a positive whole number of seconds, else <see cref="TimeSpan.Zero"/>.</summary>
    public static TimeSpan Pause =>
        int.TryParse(Environment.GetEnvironmentVariable(SecondsVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    /// <summary>Waits <see cref="Pause"/> when it is set, else returns at once.</summary>
    /// <returns>A task that completes after the pause.</returns>
    public static Task CoolDownAsync() =>
        Pause is { Ticks: > 0 } pause ? Task.Delay(pause) : Task.CompletedTask;
}
