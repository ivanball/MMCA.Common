using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Tests for gaps in individual decorator coverage and for the full
/// command decorator pipeline (Logging -> Caching -> Transactional -> Handler).
/// </summary>
public sealed class CommandDecoratorPipelineTests
{
    // ── LoggingCommandDecorator — failure Result branch (logs at Warning) ──
    [Fact]
    public async Task Logging_WhenInnerReturnsFailureResult_ReturnsFailureAndDoesNotThrow()
    {
        var inner = new Mock<ICommandHandler<PipelineTestCommand, Result>>();
        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("corr-1");
        var logger = new Mock<ILogger<LoggingCommandDecorator<PipelineTestCommand, Result>>>();

        inner.Setup(x => x.HandleAsync(It.IsAny<PipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("ERR001", "Something went wrong")));

        var sut = new LoggingCommandDecorator<PipelineTestCommand, Result>(
            inner.Object, correlationCtx.Object, logger.Object);

        var result = await sut.HandleAsync(new PipelineTestCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "ERR001");
    }

    [Fact]
    public async Task Logging_PassesCancellationTokenToInnerHandler()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        var inner = new Mock<ICommandHandler<PipelineTestCommand, Result>>();
        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("corr-1");
        var logger = new Mock<ILogger<LoggingCommandDecorator<PipelineTestCommand, Result>>>();

        inner.Setup(x => x.HandleAsync(It.IsAny<PipelineTestCommand>(), token))
            .ReturnsAsync(Result.Success());

        var sut = new LoggingCommandDecorator<PipelineTestCommand, Result>(
            inner.Object, correlationCtx.Object, logger.Object);

        await sut.HandleAsync(new PipelineTestCommand(), token);

        inner.Verify(x => x.HandleAsync(It.IsAny<PipelineTestCommand>(), token), Times.Once);
    }

    // ── CachingCommandDecorator — exception propagation ──
    [Fact]
    public async Task Caching_WhenInnerThrows_PropagatesExceptionWithoutInvalidating()
    {
        var inner = new Mock<ICommandHandler<CachePipelineTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();

        inner.Setup(x => x.HandleAsync(It.IsAny<CachePipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db failure"));

        var sut = new CachingCommandDecorator<CachePipelineTestCommand, Result>(
            inner.Object, cacheService.Object);

        Func<Task> act = () => sut.HandleAsync(new CachePipelineTestCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db failure");
        cacheService.Verify(
            x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── TransactionalCommandDecorator — failure Result still commits ──
    [Fact]
    public async Task Transactional_WhenHandlerReturnsFailureResult_StillCommitsTransaction()
    {
        var inner = new Mock<ICommandHandler<TransactionalPipelineTestCommand, Result>>();
        var unitOfWork = new Mock<IUnitOfWork>();

        inner.Setup(x => x.HandleAsync(It.IsAny<TransactionalPipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("BIZ001", "Invariant violated")));

        var sut = new TransactionalCommandDecorator<TransactionalPipelineTestCommand, Result>(
            inner.Object, unitOfWork.Object);

        var result = await sut.HandleAsync(new TransactionalPipelineTestCommand());

        // Transaction commits because the handler returned normally (no exception).
        // Business failures should be handled by callers; only exceptions trigger rollback.
        result.IsFailure.Should().BeTrue();
        unitOfWork.Verify(x => x.BeginTransaction(), Times.Once);
        unitOfWork.Verify(x => x.CommitTransaction(), Times.Once);
        unitOfWork.Verify(x => x.RollbackTransaction(), Times.Never);
    }

    // ── Full pipeline: Logging -> Caching -> Transactional -> Handler ──
    [Fact]
    public async Task Pipeline_SuccessPath_ExecutesDecoratorsInCorrectOrder()
    {
        // Track execution order
        var callOrder = new List<string>();

        // Inner handler
        var innerHandler = new Mock<ICommandHandler<FullPipelineTestCommand, Result>>();
        innerHandler
            .Setup(x => x.HandleAsync(It.IsAny<FullPipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                callOrder.Add("Handler");
                await Task.CompletedTask;
                return Result.Success();
            });

        // UnitOfWork tracks begin/commit
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransaction()).Callback(() => callOrder.Add("BeginTransaction"));
        unitOfWork.Setup(x => x.CommitTransaction()).Callback(() => callOrder.Add("CommitTransaction"));

        // CacheService tracks invalidation
        var cacheService = new Mock<ICacheService>();
        cacheService
            .Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("CacheInvalidation"));

        // Correlation context
        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("pipe-corr-1");

        // Logger
        var logger = new Mock<ILogger<LoggingCommandDecorator<FullPipelineTestCommand, Result>>>();

        // Build the pipeline: innermost -> outermost (same order as DI registration)
        // 1. Transactional wraps handler
        ICommandHandler<FullPipelineTestCommand, Result> pipeline =
            new TransactionalCommandDecorator<FullPipelineTestCommand, Result>(
                innerHandler.Object, unitOfWork.Object);

        // 2. Caching wraps transactional
        pipeline = new CachingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, cacheService.Object);

        // 3. Logging wraps caching (outermost)
        pipeline = new LoggingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, correlationCtx.Object, logger.Object);

        // Act
        var result = await pipeline.HandleAsync(new FullPipelineTestCommand());

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Expected order: Begin Txn -> Handler -> Commit Txn -> Cache Invalidation
        // Logging is outermost but only logs (no side-effects we track in callOrder).
        // Cache invalidation occurs AFTER the transaction commits (by design).
        callOrder.Should().Equal(
            "BeginTransaction",
            "Handler",
            "CommitTransaction",
            "CacheInvalidation");
    }

    [Fact]
    public async Task Pipeline_WhenHandlerThrows_RollsBackAndSkipsCacheInvalidation()
    {
        var callOrder = new List<string>();

        var innerHandler = new Mock<ICommandHandler<FullPipelineTestCommand, Result>>();
        innerHandler
            .Setup(x => x.HandleAsync(It.IsAny<FullPipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .Returns<FullPipelineTestCommand, CancellationToken>((_, _) =>
            {
                callOrder.Add("Handler");
                throw new InvalidOperationException("handler exploded");
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransaction()).Callback(() => callOrder.Add("BeginTransaction"));
        unitOfWork.Setup(x => x.CommitTransaction()).Callback(() => callOrder.Add("CommitTransaction"));
        unitOfWork.Setup(x => x.RollbackTransaction()).Callback(() => callOrder.Add("RollbackTransaction"));

        var cacheService = new Mock<ICacheService>();
        cacheService
            .Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("CacheInvalidation"));

        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("pipe-corr-2");
        var logger = new Mock<ILogger<LoggingCommandDecorator<FullPipelineTestCommand, Result>>>();

        ICommandHandler<FullPipelineTestCommand, Result> pipeline =
            new TransactionalCommandDecorator<FullPipelineTestCommand, Result>(
                innerHandler.Object, unitOfWork.Object);
        pipeline = new CachingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, cacheService.Object);
        pipeline = new LoggingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, correlationCtx.Object, logger.Object);

        Func<Task> act = () => pipeline.HandleAsync(new FullPipelineTestCommand());

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Rollback happens; no commit; no cache invalidation
        callOrder.Should().Equal(
            "BeginTransaction",
            "Handler",
            "RollbackTransaction");
    }

    [Fact]
    public async Task Pipeline_WhenHandlerReturnsFailure_CommitsButSkipsCacheInvalidation()
    {
        var callOrder = new List<string>();

        var innerHandler = new Mock<ICommandHandler<FullPipelineTestCommand, Result>>();
        innerHandler
            .Setup(x => x.HandleAsync(It.IsAny<FullPipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                callOrder.Add("Handler");
                await Task.CompletedTask;
                return Result.Failure(Error.Failure("BIZ", "business rule violated"));
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransaction()).Callback(() => callOrder.Add("BeginTransaction"));
        unitOfWork.Setup(x => x.CommitTransaction()).Callback(() => callOrder.Add("CommitTransaction"));

        var cacheService = new Mock<ICacheService>();
        cacheService
            .Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("CacheInvalidation"));

        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("pipe-corr-3");
        var logger = new Mock<ILogger<LoggingCommandDecorator<FullPipelineTestCommand, Result>>>();

        ICommandHandler<FullPipelineTestCommand, Result> pipeline =
            new TransactionalCommandDecorator<FullPipelineTestCommand, Result>(
                innerHandler.Object, unitOfWork.Object);
        pipeline = new CachingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, cacheService.Object);
        pipeline = new LoggingCommandDecorator<FullPipelineTestCommand, Result>(
            pipeline, correlationCtx.Object, logger.Object);

        var result = await pipeline.HandleAsync(new FullPipelineTestCommand());

        result.IsFailure.Should().BeTrue();

        // Transaction commits (no exception), but cache is NOT invalidated (failure result)
        callOrder.Should().Equal(
            "BeginTransaction",
            "Handler",
            "CommitTransaction");
    }

    [Fact]
    public async Task Pipeline_NonTransactionalNonCachingCommand_BypassesBothDecorators()
    {
        var callOrder = new List<string>();

        var innerHandler = new Mock<ICommandHandler<PipelineTestCommand, Result>>();
        innerHandler
            .Setup(x => x.HandleAsync(It.IsAny<PipelineTestCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                callOrder.Add("Handler");
                await Task.CompletedTask;
                return Result.Success();
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.BeginTransaction()).Callback(() => callOrder.Add("BeginTransaction"));

        var cacheService = new Mock<ICacheService>();
        cacheService
            .Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("CacheInvalidation"));

        var correlationCtx = new Mock<ICorrelationContext>();
        correlationCtx.Setup(x => x.CorrelationId).Returns("pipe-corr-4");
        var logger = new Mock<ILogger<LoggingCommandDecorator<PipelineTestCommand, Result>>>();

        ICommandHandler<PipelineTestCommand, Result> pipeline =
            new TransactionalCommandDecorator<PipelineTestCommand, Result>(
                innerHandler.Object, unitOfWork.Object);
        pipeline = new CachingCommandDecorator<PipelineTestCommand, Result>(
            pipeline, cacheService.Object);
        pipeline = new LoggingCommandDecorator<PipelineTestCommand, Result>(
            pipeline, correlationCtx.Object, logger.Object);

        var result = await pipeline.HandleAsync(new PipelineTestCommand());

        result.IsSuccess.Should().BeTrue();

        // No transaction, no cache invalidation — only the handler runs
        callOrder.Should().Equal("Handler");
        unitOfWork.Verify(x => x.BeginTransaction(), Times.Never);
        cacheService.Verify(
            x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

// ── Test types (must be public for Moq DynamicProxy) ──

/// <summary>Plain command — neither transactional nor cache-invalidating.</summary>
public sealed record PipelineTestCommand;

/// <summary>Cache-invalidating command for single-decorator cache tests.</summary>
public sealed record CachePipelineTestCommand : ICacheInvalidating
{
    public string CachePrefix => "pipeline-test";
}

/// <summary>Transactional command for single-decorator transaction tests.</summary>
public sealed record TransactionalPipelineTestCommand : ITransactional;

/// <summary>Command that opts into both transactional and cache-invalidating behavior.</summary>
public sealed record FullPipelineTestCommand : ITransactional, ICacheInvalidating
{
    public string CachePrefix => "full-pipeline";
}
