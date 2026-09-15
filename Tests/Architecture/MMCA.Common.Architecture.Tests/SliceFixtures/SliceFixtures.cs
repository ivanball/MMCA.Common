using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.SliceFixtures;

/// <summary>
/// Compiled slice shapes for <c>SliceCohesionFitnessTests</c>. Every handler here is ABSTRACT on
/// purpose: the rule used to read concrete classes only, so these types are exactly what it started
/// seeing. They are query handlers (never <c>ICommandHandler</c>) so the command-validator coverage
/// rule, which points a map at this same assembly and asserts an exact command inventory, is not
/// affected by them.
/// </summary>
internal sealed record GetFixturePreferencesQuery(int UserId);

/// <summary>
/// An abstract base whose contract is a concrete type from this assembly, declared beside it. This is
/// the shape a consumer derives from, and the co-located case the rule must pass.
/// </summary>
internal abstract class GetFixturePreferencesHandlerBase : IQueryHandler<GetFixturePreferencesQuery, Result>
{
    public Task<Result> HandleAsync(
        GetFixturePreferencesQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>
/// An abstract base parameterized over its contract, the shape of the framework's own
/// <c>*HandlerBase</c> types: <c>TQuery</c> is a generic parameter, not a type anyone can co-locate,
/// so the rule must skip it.
/// </summary>
/// <typeparam name="TQuery">The query the derived handler serves.</typeparam>
internal abstract class GetFixtureEntityHandlerBase<TQuery> : IQueryHandler<TQuery, Result>
{
    public Task<Result> HandleAsync(TQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
