using FluentValidation;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.CommandValidatorFixtures;

/// <summary>
/// Compiled command/handler/validator shapes for <c>CommandValidatorCoverageFitnessTests</c>: a
/// command covered by its own validator, one covered only through the CommandRequestValidator bridge,
/// one covered by neither, one that carries no payload at all, and a bridge command whose request has
/// no validator (the bridge half that resolves nothing and therefore validates nothing).
/// </summary>
internal sealed record CreateTicketCommand(string Title);

/// <summary>Direct coverage: an explicit validator for the command itself.</summary>
internal sealed class CreateTicketCommandValidator : AbstractValidator<CreateTicketCommand>
{
    internal CreateTicketCommandValidator() => RuleFor(c => c.Title).NotEmpty();
}

internal sealed class CreateTicketHandler : ICommandHandler<CreateTicketCommand, Result>
{
    public Task<Result> HandleAsync(CreateTicketCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>The request a bridged command embeds.</summary>
internal sealed record UpdateTicketRequest(string Title);

/// <summary>Bridge coverage: no validator of its own, but its request has one.</summary>
internal sealed record UpdateTicketCommand(UpdateTicketRequest Request) : ICommandWithRequest<UpdateTicketRequest>;

internal sealed class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    internal UpdateTicketRequestValidator() => RuleFor(r => r.Title).NotEmpty();
}

internal sealed class UpdateTicketHandler : ICommandHandler<UpdateTicketCommand, Result>
{
    public Task<Result> HandleAsync(UpdateTicketCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>The request a half-bridged command embeds. Deliberately has no validator.</summary>
internal sealed record ReopenTicketRequest(string Reason);

/// <summary>
/// Implements the bridge marker but its request has no validator, so the auto-registered
/// CommandRequestValidator resolves nothing and adds no rule. Half a bridge is not coverage.
/// </summary>
internal sealed record ReopenTicketCommand(ReopenTicketRequest Request) : ICommandWithRequest<ReopenTicketRequest>;

internal sealed class ReopenTicketHandler : ICommandHandler<ReopenTicketCommand, Result>
{
    public Task<Result> HandleAsync(ReopenTicketCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Carries data, has a handler, has no validation of any kind.</summary>
internal sealed record ArchiveTicketCommand(Guid TicketId);

internal sealed class ArchiveTicketHandler : ICommandHandler<ArchiveTicketCommand, Result>
{
    public Task<Result> HandleAsync(ArchiveTicketCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Unvalidated too, and the fixture the allowlist test exempts.</summary>
internal sealed record PurgeTicketsCommand(DateTime OlderThan);

internal sealed class PurgeTicketsHandler : ICommandHandler<PurgeTicketsCommand, Result>
{
    public Task<Result> HandleAsync(PurgeTicketsCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>A marker command with no payload: nothing to validate, so the rule skips it.</summary>
internal sealed record RebuildTicketIndexCommand();

internal sealed class RebuildTicketIndexHandler : ICommandHandler<RebuildTicketIndexCommand, Result>
{
    public Task<Result> HandleAsync(RebuildTicketIndexCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
