using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.UseCases;

public sealed class DeleteEntityHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<TestAggregateEntity, int>> _repository = new();

    public DeleteEntityHandlerTests() =>
        _unitOfWork.Setup(u => u.GetRepository<TestAggregateEntity, int>())
            .Returns(_repository.Object);

    [Fact]
    public async Task HandleAsync_WhenEntityExists_DeletesAndSaves()
    {
        var entity = new TestAggregateEntity { Id = 1 };
        _repository.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new DeleteEntityHandler<TestAggregateEntity, int>(_unitOfWork.Object);

        var result = await sut.HandleAsync(new DeleteEntityCommand<TestAggregateEntity, int>(1));

        result.IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenEntityNotFound_ReturnsNotFoundFailure()
    {
        _repository.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAggregateEntity?)null);

        var sut = new DeleteEntityHandler<TestAggregateEntity, int>(_unitOfWork.Object);

        var result = await sut.HandleAsync(new DeleteEntityCommand<TestAggregateEntity, int>(99));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenEntityAlreadyDeleted_ReturnsFailureAndDoesNotSave()
    {
        var entity = new TestAggregateEntity { Id = 1 };
        entity.Delete(); // first deletion succeeds
        _repository.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new DeleteEntityHandler<TestAggregateEntity, int>(_unitOfWork.Object);

        var result = await sut.HandleAsync(new DeleteEntityCommand<TestAggregateEntity, int>(1));

        result.IsFailure.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToRepository()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _repository.Setup(r => r.GetByIdAsync(1, token))
            .ReturnsAsync((TestAggregateEntity?)null);

        var sut = new DeleteEntityHandler<TestAggregateEntity, int>(_unitOfWork.Object);

        await sut.HandleAsync(new DeleteEntityCommand<TestAggregateEntity, int>(1), token);

        _repository.Verify(r => r.GetByIdAsync(1, token), Times.Once);
    }
}

// ── Test aggregate entity (must be public for Moq DynamicProxy) ──
public class TestAggregateEntity : AuditableAggregateRootEntity<int>;
