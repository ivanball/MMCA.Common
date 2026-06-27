using AwesomeAssertions;
using MMCA.Common.Aspire.Warmup;

namespace MMCA.Common.Aspire.Tests.Warmup;

/// <summary>
/// The readiness gate (rubric §29): closed until warm-up finishes, then latched open idempotently
/// and safely under concurrent access.
/// </summary>
public sealed class WarmupReadinessGateTests
{
    [Fact]
    public void IsReady_DefaultsToFalse() =>
        new WarmupReadinessGate().IsReady.Should().BeFalse();

    [Fact]
    public void MarkReady_OpensTheGate()
    {
        var gate = new WarmupReadinessGate();

        gate.MarkReady();

        gate.IsReady.Should().BeTrue();
    }

    [Fact]
    public void MarkReady_IsIdempotent()
    {
        var gate = new WarmupReadinessGate();

        gate.MarkReady();
        gate.MarkReady();

        gate.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task MarkReady_IsThreadSafe_UnderConcurrentCalls()
    {
        var gate = new WarmupReadinessGate();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(gate.MarkReady)));

        gate.IsReady.Should().BeTrue();
    }
}
