using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EFReadRepositoryDecoratorAdditionalTests
{
    private readonly Mock<IReadRepository<FakeEntity, int>> _inner = new();

    private EFReadRepositoryDecorator<FakeEntity, int> CreateSut() => new(_inner.Object);

    [Fact]
    public async Task GetByIdsAsync_DelegatesToInner()
    {
        IReadOnlyCollection<FakeEntity> entities = [new FakeEntity { Id = 1 }, new FakeEntity { Id = 2 }];
        _inner.Setup(x => x.GetByIdsAsync(
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<IEnumerable<string>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var result = await CreateSut().GetByIdsAsync([1, 2]);

        result.Should().HaveCount(2);
        _inner.Verify(x => x.GetByIdsAsync(
            It.IsAny<IEnumerable<int>>(),
            It.IsAny<IEnumerable<string>>(),
            false,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProjectedAsync_DelegatesToInner()
    {
        Expression<Func<FakeEntity, string>> select = e => e.Name;
        IReadOnlyCollection<string> projected = ["Test"];
        _inner.Setup(x => x.GetProjectedAsync(
                It.IsAny<Expression<Func<FakeEntity, string>>>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await CreateSut().GetProjectedAsync(select);

        result.Should().ContainSingle().Which.Should().Be("Test");
    }

    [Fact]
    public async Task GetByIdAsync_WithIncludes_DelegatesToInner()
    {
        var entity = new FakeEntity { Id = 1 };
        _inner.Setup(x => x.GetByIdAsync(1, It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        string[] includes = ["Navigation"];
        var result = await CreateSut().GetByIdAsync(1, includes);

        result.Should().BeSameAs(entity);
    }

    [Fact]
    public async Task ExistsAsync_ById_WithIgnoreQueryFilters_DelegatesToInner()
    {
        _inner.Setup(x => x.ExistsAsync(1, true, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateSut().ExistsAsync(1, ignoreQueryFilters: true);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ByPredicate_WithIgnoreQueryFilters_DelegatesToInner()
    {
        Expression<Func<FakeEntity, bool>> where = e => e.Id == 1;
        _inner.Setup(x => x.ExistsAsync(
                It.IsAny<Expression<Func<FakeEntity, bool>>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateSut().ExistsAsync(where, ignoreQueryFilters: true);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task FirstOrDefaultAsync_ByPredicate_DelegatesToInner()
    {
        var entity = new FakeEntity { Id = 1 };
        Expression<Func<FakeEntity, bool>> where = e => e.Id == 1;
        string[] includes = ["Navigation"];
        _inner.Setup(x => x.FirstOrDefaultAsync(
                where, includes, true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await CreateSut().FirstOrDefaultAsync(where, includes, asTracking: true, ignoreQueryFilters: true);

        result.Should().BeSameAs(entity);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_BySpecification_DelegatesToInner()
    {
        var entity = new FakeEntity { Id = 2 };
        _inner.Setup(x => x.FirstOrDefaultAsync(
                It.IsAny<ISpecification<FakeEntity, int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var result = await CreateSut().FirstOrDefaultAsync(
            new InlineSpecification<FakeEntity, int>(e => e.Id == 2));

        result.Should().BeSameAs(entity);
    }

    [Fact]
    public async Task CountByAsync_DelegatesToInner()
    {
        IReadOnlyDictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 2 };
        _inner.Setup(x => x.CountByAsync(
                It.IsAny<Expression<Func<FakeEntity, string>>>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(counts);

        var result = await CreateSut().CountByAsync(e => e.Name);

        result.Should().BeSameAs(counts);
    }

    [Fact]
    public async Task SumByAsync_DelegatesToInner()
    {
        IReadOnlyDictionary<string, decimal> totals = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["a"] = 3m };
        _inner.Setup(x => x.SumByAsync(
                It.IsAny<Expression<Func<FakeEntity, string>>>(),
                It.IsAny<Expression<Func<FakeEntity, decimal>>>(),
                It.IsAny<Expression<Func<FakeEntity, bool>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(totals);

        var result = await CreateSut().SumByAsync(e => e.Name, e => e.Id);

        result.Should().BeSameAs(totals);
    }

    [Fact]
    public async Task FindIncludingDeletedAsync_DelegatesToInner()
    {
        IReadOnlyCollection<FakeEntity> active = [new FakeEntity { Id = 1 }];
        IReadOnlyCollection<FakeEntity> softDeleted = [new FakeEntity { Id = 2 }];
        _inner.Setup(x => x.FindIncludingDeletedAsync(
                It.IsAny<Expression<Func<FakeEntity, bool>>>(),
                It.IsAny<IEnumerable<string>?>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((active, softDeleted));

        var result = await CreateSut().FindIncludingDeletedAsync(e => e.Id > 0, asTracking: true);

        result.Active.Should().BeSameAs(active);
        result.SoftDeleted.Should().BeSameAs(softDeleted);
    }

    public sealed class FakeEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }
}
