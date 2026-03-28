using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

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
    public async Task UpdateAsync_DelegatesToInner()
    {
        var entity = new FakeAggregateEntity { Id = 1 };
        _inner.Setup(x => x.UpdateAsync(entity, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateSut().UpdateAsync(entity);

        _inner.Verify(x => x.UpdateAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveChangesAsync_DelegatesToInner()
    {
        _inner.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await CreateSut().SaveChangesAsync();

        result.Should().Be(3);
    }

    [Fact]
    public void Save_DelegatesToInner()
    {
        _inner.Setup(x => x.Save()).Returns(5);

        var result = CreateSut().Save();

        result.Should().Be(5);
    }

    [Fact]
    public void Constructor_WithNullInner_ThrowsArgumentNullException()
    {
        var act = () => new EFRepositoryDecorator<FakeAggregateEntity, int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    public sealed class FakeAggregateEntity : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }
}
