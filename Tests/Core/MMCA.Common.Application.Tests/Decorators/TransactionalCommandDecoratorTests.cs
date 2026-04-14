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
        unitOfWork.Verify(
            x => x.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<Result>>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Transactional command delegates to ExecuteInTransactionAsync ──
    [Fact]
    public async Task HandleAsync_TransactionalCommand_BeginsAndCommitsTransaction()
    {
        var inner = new Mock<ICommandHandler<TransactionalCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TransactionalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        SetupExecuteInTransaction(unitOfWork);

        var sut = new TransactionalCommandDecorator<TransactionalCommand, Result>(inner.Object, unitOfWork.Object);

        var result = await sut.HandleAsync(new TransactionalCommand());

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Verify(
            x => x.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<Result>>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Transactional command propagates exceptions through ExecuteInTransactionAsync ──
    [Fact]
    public async Task HandleAsync_TransactionalCommand_WhenHandlerThrows_RollsBack()
    {
        var inner = new Mock<ICommandHandler<TransactionalCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TransactionalCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));
        SetupExecuteInTransaction(unitOfWork);

        var sut = new TransactionalCommandDecorator<TransactionalCommand, Result>(inner.Object, unitOfWork.Object);

        var act = () => sut.HandleAsync(new TransactionalCommand());

        await act.Should().ThrowAsync<InvalidOperationException>();
        unitOfWork.Verify(
            x => x.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<Result>>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Sets up the <see cref="IUnitOfWork.ExecuteInTransactionAsync{TResult}"/> mock to
    /// simply invoke the callback — the real begin/commit/rollback is an implementation
    /// detail of <c>DbContextFactory</c>.
    /// </summary>
    private static void SetupExecuteInTransaction(Mock<IUnitOfWork> unitOfWork) =>
        unitOfWork.Setup(x => x.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task<Result>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task<Result>>, CancellationToken>(
                (operation, ct) => operation(ct));
}

// ── Test types (must be public for Moq DynamicProxy) ──
public sealed record NonTransactionalCommand;

public sealed record TransactionalCommand : ITransactional;
