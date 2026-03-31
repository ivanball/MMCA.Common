using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Extensions;

public sealed class ReadRepositoryExtensionsTests
{
    // ── Entity found ──
    [Fact]
    public async Task GetByIdOrFailAsync_WhenEntityExists_ReturnsSuccessWithEntity()
    {
        var repository = new Mock<IReadRepository<TestReadEntity, int>>();
        var entity = new TestReadEntity { Id = 1 };
        IReadOnlyCollection<TestReadEntity> entities = [entity];

        repository.Setup(x => x.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<TestReadEntity, bool>>>(),
                It.IsAny<Expression<Func<TestReadEntity, string>>>(),
                It.IsAny<Expression<Func<TestReadEntity, TestReadEntity>>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        Result<TestReadEntity> result = await repository.Object.GetByIdOrFailAsync(
            1, "TestSource");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(entity);
    }

    // ── Entity not found ──
    [Fact]
    public async Task GetByIdOrFailAsync_WhenEntityDoesNotExist_ReturnsNotFoundFailure()
    {
        var repository = new Mock<IReadRepository<TestReadEntity, int>>();
        IReadOnlyCollection<TestReadEntity> emptyEntities = [];

        repository.Setup(x => x.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<TestReadEntity, bool>>>(),
                It.IsAny<Expression<Func<TestReadEntity, string>>>(),
                It.IsAny<Expression<Func<TestReadEntity, TestReadEntity>>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyEntities);

        Result<TestReadEntity> result = await repository.Object.GetByIdOrFailAsync(
            999, "TestSource");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Type.Should().Be(ErrorType.NotFound);
    }
}

// ── Test helpers ──
public sealed class TestReadEntity : AuditableBaseEntity<int>
{
}
