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
/// <para>
/// Every case records the <see cref="PromptContract.Hash"/> it was recorded against, and
/// <see cref="EveryGoldenCase_WasRecordedAgainstTheCurrentContract"/> fails, naming the cases, when
/// that hash is not <see cref="CurrentContract"/>'s. A recording is evidence about one exact
/// prompt, model and system text; once any of them moves, replaying the old answer still passes
/// while proving nothing, so the gate demands a re-recording instead.
/// </para>
/// </summary>
public abstract class GoldenReplayTestsBase
{
    /// <summary>Gets the corpus: one entry per recorded case.</summary>
    protected abstract IEnumerable<GoldenReplayCase> Cases { get; }

    /// <summary>
    /// Gets the prompt contract the code under test ships today: the same instance it sends with
    /// its requests, so the hash compared against each case is the hash in production.
    /// </summary>
    protected abstract PromptContract CurrentContract { get; }

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
    public void EveryGoldenCase_WasRecordedAgainstTheCurrentContract()
    {
        var cases = LoadCases();
        var contract = CurrentContract
            ?? throw new InvalidOperationException(
                "CurrentContract is null, so no recording can be checked against the prompt that ships. "
                + "Return the PromptContract the code under test sends.");
        var currentHash = contract.Hash;

        var stale = cases
            .Where(testCase => testCase is not null
                && !string.Equals(testCase.PromptContractHash, currentHash, StringComparison.Ordinal))
            .Select(testCase => $"  - {testCase.Id}: recorded against {testCase.PromptContractHash}")
            .ToList();

        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{stale.Count} of {cases.Count} golden cases were recorded against a different prompt contract than {contract.Name}@{contract.Version} (hash {currentHash}). A recording proves nothing about a prompt, model or system text it was not made with: re-record these cases against the current contract and set their PromptContractHash to {currentHash}.{Environment.NewLine}{string.Join(Environment.NewLine, stale)}"));
        }
    }

    [Fact]
    public async Task EveryGoldenCase_ReplaysAsRecorded()
    {
        var cases = LoadCases();
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var failure = await RunCaseAsync(testCase, TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{failures.Count} of {cases.Count} golden cases did not replay as recorded:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"));
        }
    }

    private List<GoldenReplayCase> LoadCases()
    {
        List<GoldenReplayCase> cases = Cases is null ? [] : [.. Cases];

        if (cases.Count == 0)
        {
            throw new InvalidOperationException(
                "The golden corpus is empty, so this gate would pass without evaluating anything. "
                + "Record at least one case and declare it in Cases.");
        }

        return cases;
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
