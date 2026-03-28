using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Tests.Services;

public sealed class NullNavigationPopulatorTests
{
    private sealed class StubEntity : AuditableBaseEntity<int>;

    [Fact]
    public async Task PopulateAsync_CompletesImmediately()
    {
        var populator = new NullNavigationPopulator<StubEntity>();
        IReadOnlyCollection<StubEntity> entities = [new StubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();

        var task = populator.PopulateAsync(entities, metadata, true, true, CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
    }

    [Fact]
    public async Task PopulateAsync_WithEmptyCollection_CompletesImmediately()
    {
        var populator = new NullNavigationPopulator<StubEntity>();
        IReadOnlyCollection<StubEntity> entities = [];
        var metadata = new NavigationMetadata();

        var task = populator.PopulateAsync(entities, metadata, false, false, CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
    }

    [Fact]
    public void PopulateAsync_ImplementsINavigationPopulator() =>
        new NullNavigationPopulator<StubEntity>().Should().BeAssignableTo<INavigationPopulator<StubEntity>>();
}
