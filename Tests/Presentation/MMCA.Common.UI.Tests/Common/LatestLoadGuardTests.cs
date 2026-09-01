using AwesomeAssertions;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// The stale-response guard for routed pages: Blazor reuses the component instance across route
/// parameter changes, so a slow load for one id must not overwrite the page after a faster load for
/// the next id has already rendered.
/// </summary>
public sealed class LatestLoadGuardTests
{
    [Fact]
    public void Begin_CancelsThePreviousLoad()
    {
        using var sut = new LatestLoadGuard();

        var (first, _) = sut.Begin();
        first.IsCancellationRequested.Should().BeFalse();

        var (second, _) = sut.Begin();

        first.IsCancellationRequested.Should().BeTrue("the superseded fetch has no reader left");
        second.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void IsCurrent_IsFalseForASupersededGenerationAndTrueForTheLatest()
    {
        using var sut = new LatestLoadGuard();

        var (_, first) = sut.Begin();
        var (_, second) = sut.Begin();

        sut.IsCurrent(first).Should().BeFalse("its result would overwrite the page with the wrong entity");
        sut.IsCurrent(second).Should().BeTrue();
    }

    [Fact]
    public void IsCurrent_IsTrueForASingleLoad()
    {
        using var sut = new LatestLoadGuard();

        var (_, generation) = sut.Begin();

        sut.IsCurrent(generation).Should().BeTrue();
    }

    [Fact]
    public void Dispose_CancelsTheInFlightLoadAndInvalidatesItsGeneration()
    {
        var sut = new LatestLoadGuard();
        var (token, generation) = sut.Begin();

        sut.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        sut.IsCurrent(generation).Should().BeFalse("a disposed component assigns nothing");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sut = new LatestLoadGuard();
        sut.Begin();

        sut.Dispose();
        var act = sut.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Begin_AfterDispose_Throws()
    {
        var sut = new LatestLoadGuard();
        sut.Dispose();

        var act = () => sut.Begin();

        act.Should().Throw<ObjectDisposedException>();
    }
}
