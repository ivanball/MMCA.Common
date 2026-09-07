using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="EfCoreConcurrencyConflictDetector"/>: the Application-layer question
/// "did another writer move the row's token" answered from the provider type, so a handler that
/// claims work by writing a row never catches an EF exception itself.
/// </summary>
public sealed class EfCoreConcurrencyConflictDetectorTests
{
    private readonly EfCoreConcurrencyConflictDetector _sut = new();

    [Fact]
    public void IsConcurrencyConflict_RecognisesTheProviderException()
        => _sut.IsConcurrencyConflict(new DbUpdateConcurrencyException("stale")).Should().BeTrue();

    [Fact]
    public void IsConcurrencyConflict_WalksTheInnerExceptionChain()
    {
        // A save wrapped by a transaction or a retrying execution strategy carries the real cause
        // underneath, so testing the outermost exception alone would miss it.
        var nested = new InvalidOperationException("retry strategy", new DbUpdateConcurrencyException("stale"));

        _sut.IsConcurrencyConflict(nested).Should().BeTrue();

        var deeper = new InvalidOperationException("outer", nested);
        _sut.IsConcurrencyConflict(deeper).Should().BeTrue();
    }

    [Fact]
    public void IsConcurrencyConflict_DoesNotClassifyOtherSaveFailures()
    {
        _sut.IsConcurrencyConflict(new DbUpdateException("fk violation")).Should().BeFalse();
        _sut.IsConcurrencyConflict(new TimeoutException()).Should().BeFalse();
    }
}
