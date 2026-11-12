using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Outages;

namespace MMCA.Common.Testing.Aspire.Tests.Outages;

/// <summary>
/// Ruling L4 (design rule 29.12): the start command <c>RunOutageAsync</c> issues from its
/// <see langword="finally"/> after a failure is best effort and bounded. It runs under its own budget, whatever it
/// throws is swallowed, and a command that never finishes is abandoned when the budget runs out, so the
/// ORIGINAL failure is the one that propagates. What needs a running application (that the base's
/// <see langword="finally"/> really issues the orchestrator's start command) is covered by the AppHost tier only.
/// </summary>
public sealed class OutageCommandsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task AnyException_IsSwallowed()
    {
        var act = () => OutageCommands.RunBestEffortAsync(
            _ => throw new ArgumentException("Unknown resource 'broker'."),
            Budget);

        await act.Should().NotThrowAsync("an ArgumentException or a transport exception must not replace the original failure");
    }

    [Fact]
    public async Task AFaultedCommand_IsSwallowed()
    {
        var act = () => OutageCommands.RunBestEffortAsync(
            _ => Task.FromException(new IOException("The orchestrator connection was reset.")),
            Budget);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ACommandThatIgnoresItsToken_IsAbandonedWhenTheBudgetRunsOut()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await OutageCommands.RunBestEffortAsync(_ => never.Task, Budget)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "a wedged start must not hang the test until the CI job timeout");
    }

    [Fact]
    public async Task TheCommand_GetsATokenCancelledAtTheBudget()
    {
        var observed = CancellationToken.None;

        await OutageCommands.RunBestEffortAsync(
            async token =>
            {
                observed = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            Budget)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        observed.CanBeCanceled.Should().BeTrue();
        observed.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task TheOriginalException_PropagatesUnchanged()
    {
        var original = new InvalidOperationException("the assertion that failed while the dependency was down");
        var act = async () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                await OutageCommands.RunBestEffortAsync(FailingRestart, Budget);
            }
        };

        static Task FailingRestart(CancellationToken cancellationToken) =>
            throw new ArgumentException("the restart failed too");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(original);
    }

    [Fact]
    public async Task ANonPositiveBudget_IsRejected()
    {
        var act = () => OutageCommands.RunBestEffortAsync(_ => Task.CompletedTask, TimeSpan.Zero);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
