using System.Globalization;
using Xunit;

namespace MMCA.Common.AI.Testing;

/// <summary>
/// The golden-replay evaluation gate: every case in a corpus is replayed through the real code
/// under test against the answer recorded for it, with no model, no credential and no network, so
/// it runs on every CI leg.
/// <para>
/// Subclass it next to the code that calls a model, declare the corpus in <see cref="Cases"/> and
/// the per-case assertion in
/// <see cref="AssertCaseAsync(GoldenReplayCase, ReplayChatClient, CancellationToken)"/>. Each case
/// gets a FRESH <see cref="ReplayChatClient"/>, so one case cannot consume another's recording or
/// see another's request.
/// </para>
/// <para>
/// Failures are collected rather than thrown at the first one: a prompt edit usually breaks several
/// cases at once, and the useful report is all of them with their ids, not the first one
/// alphabetically. An empty corpus is a failure too, because an evaluation that evaluates nothing
/// passes forever.
/// </para>
/// </summary>
public abstract class GoldenReplayTestsBase
{
    /// <summary>Gets the corpus: one entry per recorded case.</summary>
    protected abstract IEnumerable<GoldenReplayCase> Cases { get; }

    /// <summary>
    /// Drives the code under test for one case and asserts the outcome. Throwing (an assertion
    /// failure or anything else) fails that case and is reported against its id.
    /// </summary>
    /// <param name="testCase">The case being replayed.</param>
    /// <param name="replay">A fresh replay client holding that case's recorded response.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>A task that completes when the case has been asserted.</returns>
    protected abstract Task AssertCaseAsync(
        GoldenReplayCase testCase,
        ReplayChatClient replay,
        CancellationToken cancellationToken);

    [Fact]
    public async Task EveryGoldenCase_ReplaysAsRecorded()
    {
        List<GoldenReplayCase> cases = Cases is null ? [] : [.. Cases];
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var failure = await RunCaseAsync(testCase, TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException(
                "The golden corpus is empty, so this gate would pass without evaluating anything. "
                + "Record at least one case and declare it in Cases.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{failures.Count} of {cases.Count} golden cases did not replay as recorded:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"));
        }
    }

    private async Task<string?> RunCaseAsync(GoldenReplayCase testCase, CancellationToken cancellationToken)
    {
        if (testCase is null)
        {
            return "  - (null case): the corpus holds a null entry.";
        }

        // The assertion body is arbitrary consumer code, so every exception it can raise is a
        // failure of THAT case and has to be reported against its id rather than ending the run.
#pragma warning disable CA1031, S2221 // A failing case must be reported, not end the run.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, testCase.ResponsePath);
            var recorded = RecordedResponses.Read(path);

            using var replay = new ReplayChatClient(recorded);
            await AssertCaseAsync(testCase, replay, cancellationToken).ConfigureAwait(false);

            return null;
        }
        catch (Exception ex)
        {
            return $"  - {testCase.Id} ({testCase.Description}): {ex.Message}";
        }
#pragma warning restore CA1031, S2221
    }
}
