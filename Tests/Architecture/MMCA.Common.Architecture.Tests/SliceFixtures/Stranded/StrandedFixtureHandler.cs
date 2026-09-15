using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.SliceFixtures.Stranded;

/// <summary>
/// An abstract handler base over a concrete contract that lives one namespace away: the violation the
/// rule is meant to catch, and the one it could not see while it read concrete classes only. It sits
/// in its own folder because the namespace has to follow the folder (IDE0130).
/// </summary>
internal abstract class StrandedFixtureHandlerBase : IQueryHandler<GetFixturePreferencesQuery, Result>
{
    public Task<Result> HandleAsync(
        GetFixturePreferencesQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
