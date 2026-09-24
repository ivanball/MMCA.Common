using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.ReadRepositoryFixtures;

// Compiled-in fixtures for QueryHandlerReadRepositoryTests: the read-repository rule reads IL, so its
// behaviour is pinned against real handler bodies. None of these is ever executed.
internal sealed class ReadRepositoryFixtureAggregate : AuditableAggregateRootEntity<int>;

internal sealed record ReadRepositoryFixtureQuery;

internal sealed record ReadRepositoryFixtureCommand;

/// <summary>A query handler that asks for the WRITE repository after an await (state-machine body).</summary>
internal sealed class WriteRepositoryQueryHandlerFixture(IUnitOfWork unitOfWork)
    : IQueryHandler<ReadRepositoryFixtureQuery, Result<int>>
{
    public async Task<Result<int>> HandleAsync(ReadRepositoryFixtureQuery query, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var repository = unitOfWork.GetRepository<ReadRepositoryFixtureAggregate, int>();
        return Result.Success(await repository.CountAsync(cancellationToken: cancellationToken));
    }
}

/// <summary>A query handler that asks for the READ repository: the shape the rule asks for.</summary>
internal sealed class ReadRepositoryQueryHandlerFixture(IUnitOfWork unitOfWork)
    : IQueryHandler<ReadRepositoryFixtureQuery, Result<int>>
{
    public async Task<Result<int>> HandleAsync(ReadRepositoryFixtureQuery query, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetReadRepository<ReadRepositoryFixtureAggregate, int>();
        return Result.Success(await repository.CountAsync(cancellationToken: cancellationToken));
    }
}

/// <summary>A COMMAND handler using the write repository: out of the rule's scope.</summary>
internal sealed class WriteRepositoryCommandHandlerFixture(IUnitOfWork unitOfWork)
    : ICommandHandler<ReadRepositoryFixtureCommand, Result>
{
    public async Task<Result> HandleAsync(ReadRepositoryFixtureCommand command, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<ReadRepositoryFixtureAggregate, int>();
        _ = await repository.CountAsync(cancellationToken: cancellationToken);
        return Result.Success();
    }
}
