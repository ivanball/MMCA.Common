using System.Linq.Expressions;
using FluentAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EFReadRepositoryDecoratorTests
{
    private readonly Mock<IReadRepository<FakeEntity, int>> _inner = new();

    private EFReadRepositoryDecorator<FakeEntity, int> CreateSut() => new(_inner.Object);

    [Fact]
    public async Task GetByIdAsync_DelegatesToInner()
    {
        var entity = new FakeEntity { Id = 1 };
        _inner.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateSut().GetByIdAsync(1);

        result.Should().BeSameAs(entity);
        _inner.Verify(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CountAsync_DelegatesToInner()
    {
        _inner.Setup(x => x.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await CreateSut().CountAsync();

        result.Should().Be(42);
    }

    [Fact]
    public async Task CountAsync_WithPredicate_DelegatesToInner()
    {
        Expression<Func<FakeEntity, bool>> where = e => e.Id > 5;
        _inner.Setup(x => x.CountAsync(It.IsAny<Expression<Func<FakeEntity, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        var result = await CreateSut().CountAsync(where);

        result.Should().Be(10);
    }

    [Fact]
    public async Task ExistsAsync_ById_DelegatesToInner()
    {
        _inner.Setup(x => x.ExistsAsync(1, false, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateSut().ExistsAsync(1);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ByPredicate_DelegatesToInner()
    {
        Expression<Func<FakeEntity, bool>> where = e => e.Id == 1;
        _inner.Setup(x => x.ExistsAsync(
                It.IsAny<Expression<Func<FakeEntity, bool>>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateSut().ExistsAsync(where);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_DelegatesToInner()
    {
        IReadOnlyCollection<FakeEntity> entities = [new FakeEntity { Id = 1 }];
        _inner.Setup(x => x.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                It.IsAny<Expression<Func<FakeEntity, string>>>(),
                It.IsAny<Expression<Func<FakeEntity, FakeEntity>>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var result = await CreateSut().GetAllAsync([]);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAllForLookupAsync_DelegatesToInner()
    {
        IReadOnlyCollection<BaseLookup<int>> lookups = [new BaseLookup<int> { Id = 1, Name = "test" }];
        _inner.Setup(x => x.GetAllForLookupAsync(
                "Name",
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookups);

        var result = await CreateSut().GetAllForLookupAsync("Name");

        result.Should().HaveCount(1);
    }

    [Fact]
    public void Table_DelegatesToInner()
    {
        var queryable = Array.Empty<FakeEntity>().AsQueryable();
        _inner.Setup(x => x.Table).Returns(queryable);

        CreateSut().Table.Should().BeSameAs(queryable);
    }

    [Fact]
    public void TableNoTracking_DelegatesToInner()
    {
        var queryable = Array.Empty<FakeEntity>().AsQueryable();
        _inner.Setup(x => x.TableNoTracking).Returns(queryable);

        CreateSut().TableNoTracking.Should().BeSameAs(queryable);
    }

    [Fact]
    public void TableNoTrackingSingleQuery_DelegatesToInner()
    {
        var queryable = Array.Empty<FakeEntity>().AsQueryable();
        _inner.Setup(x => x.TableNoTrackingSingleQuery).Returns(queryable);

        CreateSut().TableNoTrackingSingleQuery.Should().BeSameAs(queryable);
    }

    [Fact]
    public void TableNoTrackingSplitQuery_DelegatesToInner()
    {
        var queryable = Array.Empty<FakeEntity>().AsQueryable();
        _inner.Setup(x => x.TableNoTrackingSplitQuery).Returns(queryable);

        CreateSut().TableNoTrackingSplitQuery.Should().BeSameAs(queryable);
    }

    [Fact]
    public void Constructor_WithNullInner_ThrowsArgumentNullException()
    {
        var act = () => new EFReadRepositoryDecorator<FakeEntity, int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    public sealed class FakeEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }
}
