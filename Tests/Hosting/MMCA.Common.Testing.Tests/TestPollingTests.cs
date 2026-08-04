using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Covers <see cref="TestPolling.PollUntilAsync"/>: the helper every cross-service assertion goes through
/// instead of a fixed sleep. It must stop at the first satisfying probe, and on timeout it must return the
/// last probed value (so the caller's own assertion produces the failure message, not a bare timeout).
/// </summary>
public class TestPollingTests
{
    [Fact]
    public async Task PollUntilAsync_ReturnsImmediately_WhenTheFirstProbeSatisfies()
    {
        var probes = 0;

        var result = await TestPolling.PollUntilAsync(
            () =>
            {
                probes++;
                return Task.FromResult(7);
            },
            value => value == 7,
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1));

        result.Should().Be(7);
        probes.Should().Be(1);
    }

    [Fact]
    public async Task PollUntilAsync_KeepsProbing_UntilTheConditionHolds()
    {
        var probes = 0;

        var result = await TestPolling.PollUntilAsync(
            () => Task.FromResult(++probes),
            value => value >= 3,
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1));

        result.Should().Be(3);
        probes.Should().Be(3);
    }

    [Fact]
    public async Task PollUntilAsync_ReturnsTheLastProbedValue_OnTimeout()
    {
        var result = await TestPolling.PollUntilAsync(
            () => Task.FromResult("pending"),
            value => string.Equals(value, "done", StringComparison.Ordinal),
            timeout: TimeSpan.FromMilliseconds(20),
            interval: TimeSpan.FromMilliseconds(1));

        result.Should().Be("pending");
    }

    [Fact]
    public async Task PollUntilAsync_RejectsNullArguments()
    {
        var nullProbe = async () => await TestPolling.PollUntilAsync<int>(null!, _ => true);
        var nullCondition = async () => await TestPolling.PollUntilAsync(() => Task.FromResult(1), null!);

        await nullProbe.Should().ThrowAsync<ArgumentNullException>();
        await nullCondition.Should().ThrowAsync<ArgumentNullException>();
    }
}
