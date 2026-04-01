using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EFRepositoryDecoratorAdditionalTests
{
    private readonly Mock<IRepository<FakeAggregateEntity, int>> _inner = new();

    private EFRepositoryDecorator<FakeAggregateEntity, int> CreateSut() => new(_inner.Object);

    [Fact]
    public async Task AddRangeAsync_DelegatesToInner()
    {
        var entities = new List<FakeAggregateEntity> { new() { Id = 1 }, new() { Id = 2 } };
        _inner.Setup(x => x.AddRangeAsync(entities, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateSut().AddRangeAsync(entities);

        _inner.Verify(x => x.AddRangeAsync(entities, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void UpdateRange_DelegatesToInner()
    {
        var entities = new List<FakeAggregateEntity> { new() { Id = 1 } };

        CreateSut().UpdateRange(entities);

        _inner.Verify(x => x.UpdateRange(entities), Times.Once);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_DelegatesToInner()
    {
        Expression<Func<FakeAggregateEntity, bool>> where = e => e.Id > 5;
        _inner.Setup(x => x.ExecuteDeleteAsync(It.IsAny<Expression<Func<FakeAggregateEntity, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await CreateSut().ExecuteDeleteAsync(where);

        result.Should().Be(3);
    }

    public sealed class FakeAggregateEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }
}
