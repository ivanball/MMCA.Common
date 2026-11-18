using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

public sealed class EFRepositoryDecoratorTests
{
    private readonly Mock<IRepository<FakeAggregateEntity, int>> _inner = new();

    private EFRepositoryDecorator<FakeAggregateEntity, int> CreateSut() => new(_inner.Object);

    [Fact]
    public async Task AddAsync_DelegatesToInner()
    {
        var entity = new FakeAggregateEntity { Id = 1 };
        _inner.Setup(x => x.AddAsync(entity, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateSut().AddAsync(entity);

        _inner.Verify(x => x.AddAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_WithNullInner_ThrowsArgumentNullException()
    {
        var act = () => new EFRepositoryDecorator<FakeAggregateEntity, int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddRangeAsync_DelegatesToInner()
    {
        var entities = new List<FakeAggregateEntity> { new() { Id = 1 }, new() { Id = 2 } };
        _inner.Setup(x => x.AddRangeAsync(entities, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateSut().AddRangeAsync(entities);

        _inner.Verify(x => x.AddRangeAsync(entities, It.IsAny<CancellationToken>()), Times.Once);
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
