using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Architecture.Tests.SliceFixtures.Stranded;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.SliceFixtures;

/// <summary>
/// One handler class serving TWO query contracts, both declared one namespace away (L242).
/// Registration wires it for both, so the slice rule must report both stranded contracts rather
/// than whichever interface reflection happens to list first.
/// </summary>
internal abstract class FixtureTwoContractHandlerBase
    : IQueryHandler<GetFixtureFarQuery, Result>, IQueryHandler<GetFixtureSecondFarQuery, Result>
{
    public Task<Result> HandleAsync(GetFixtureFarQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> HandleAsync(GetFixtureSecondFarQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
