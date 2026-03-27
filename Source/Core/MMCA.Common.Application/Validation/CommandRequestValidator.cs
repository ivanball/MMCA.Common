using FluentValidation;
using MMCA.Common.Application.UseCases;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// Auto-registered validator for commands implementing <see cref="ICommandWithRequest{TRequest}"/>.
/// Delegates validation to the registered <c>IValidator&lt;TRequest&gt;</c> by calling
/// <see cref="FluentValidation.AbstractValidator{T}.RuleFor"/> on the <c>Request</c> property
/// with <c>SetValidator</c>.
/// <para>
/// This validator is registered automatically by
/// <see cref="DependencyInjection.ScanModuleApplicationServices{TAssemblyMarker}"/> using
/// <c>TryAdd</c> semantics — explicit command validators take precedence.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type that embeds the request.</typeparam>
/// <typeparam name="TRequest">The embedded request type containing the validated fields.</typeparam>
public sealed class CommandRequestValidator<TCommand, TRequest> : AbstractValidator<TCommand>
    where TCommand : ICommandWithRequest<TRequest>
{
    public CommandRequestValidator(IEnumerable<IValidator<TRequest>> requestValidators)
    {
        var validator = requestValidators.FirstOrDefault();
        if (validator is not null)
            RuleFor(c => c.Request).SetValidator(validator);
    }
}
