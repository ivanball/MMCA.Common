using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class TransactionalCommandDecoratorTests
{
    // ── Non-transactional command passes through without transaction ──
    [Fact]
    public async Task HandleAsync_NonTransactionalCommand_DoesNotUseTransaction()
    {
        var inner = new Mock<ICommandHandler<NonTransactionalCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        inner.Setup(x => x.HandleAsync(It.IsAny<NonTransactionalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new TransactionalCommandDecorator<NonTransactionalCommand, Result>(inner.Object, unitOfWork.Object);

        var result = await sut.HandleAsync(new NonTransactionalCommand());

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Verify(x => x.BeginTransaction(), Times.Never);
        unitOfWork.Verify(x => x.CommitTransaction(), Times.Never);
    }

    // ── Transactional command begins and commits transaction ──
    [Fact]
    public async Task HandleAsync_TransactionalCommand_BeginsAndCommitsTransaction()
    {
        var inner = new Mock<ICommandHandler<TransactionalCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TransactionalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new TransactionalCommandDecorator<TransactionalCommand, Result>(inner.Object, unitOfWork.Object);

        var result = await sut.HandleAsync(new TransactionalCommand());

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Verify(x => x.BeginTransaction(), Times.Once);
        unitOfWork.Verify(x => x.CommitTransaction(), Times.Once);
        unitOfWork.Verify(x => x.RollbackTransaction(), Times.Never);
    }

    // ── Transactional command rolls back on exception ──
    [Fact]
    public async Task HandleAsync_TransactionalCommand_WhenHandlerThrows_RollsBack()
    {
        var inner = new Mock<ICommandHandler<TransactionalCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TransactionalCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var sut = new TransactionalCommandDecorator<TransactionalCommand, Result>(inner.Object, unitOfWork.Object);

        var act = () => sut.HandleAsync(new TransactionalCommand());

        await act.Should().ThrowAsync<InvalidOperationException>();
        unitOfWork.Verify(x => x.BeginTransaction(), Times.Once);
        unitOfWork.Verify(x => x.RollbackTransaction(), Times.Once);
        unitOfWork.Verify(x => x.CommitTransaction(), Times.Never);
    }
}

// ── Test types (must be public for Moq DynamicProxy) ──
public sealed record NonTransactionalCommand;

public sealed record TransactionalCommand : ITransactional;
