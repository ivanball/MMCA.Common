using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.ConstructorDependencyFixtures;

/// <summary>
/// Compiled injection shapes for <c>ConstructorDependencyCountTestsBaseTests</c>: one fat and one thin
/// member of each population the ceiling now covers (API controllers, command handlers, query handlers).
/// They are TOP-LEVEL on purpose - the gate skips nested types, so a fixture nested inside the test
/// class would never be scanned. Every injected collaborator is read back through
/// <c>Collaborators</c> so none is an unread primary-constructor parameter (CS9113). The command is a
/// payload-free marker, which the command-validator coverage rule skips, so pointing a map at this
/// assembly still counts the same five data-carrying commands it did before.
/// </summary>
[ApiController]
internal sealed class FatFixtureController(string first, string second, string third) : ControllerBase
{
    internal string Collaborators => string.Concat(first, second, third);
}

/// <summary>One injected collaborator: inside every ceiling under test.</summary>
[ApiController]
internal sealed class ThinFixtureController(string only) : ControllerBase
{
    internal string Collaborators => only;
}

/// <summary>A payload-free marker command, so it carries nothing the validator-coverage rule counts.</summary>
internal sealed record RebuildFixtureProjectionCommand;

/// <summary>Three injected collaborators behind an <c>ICommandHandler</c>.</summary>
internal sealed class RebuildFixtureProjectionHandler(string first, string second, string third)
    : ICommandHandler<RebuildFixtureProjectionCommand, Result>
{
    internal string Collaborators => string.Concat(first, second, third);

    public Task<Result> HandleAsync(
        RebuildFixtureProjectionCommand command,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>The query the fat query handler serves, declared beside it (slice cohesion).</summary>
internal sealed record GetFixtureProjectionQuery(int ProjectionId);

/// <summary>Three injected collaborators behind an <c>IQueryHandler</c>: the other handler contract.</summary>
internal sealed class GetFixtureProjectionHandler(string first, string second, string third)
    : IQueryHandler<GetFixtureProjectionQuery, Result>
{
    internal string Collaborators => string.Concat(first, second, third);

    public Task<Result> HandleAsync(
        GetFixtureProjectionQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
