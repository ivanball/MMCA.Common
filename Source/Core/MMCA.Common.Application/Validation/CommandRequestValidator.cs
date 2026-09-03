using FluentValidation;
using MMCA.Common.Application.UseCases.Contracts;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// Auto-registered validator for commands implementing <see cref="ICommandWithRequest{TRequest}"/>.
/// Delegates validation to the registered <c>IValidator&lt;TRequest&gt;</c> instances by calling
/// <see cref="FluentValidation.AbstractValidator{T}.RuleFor"/> on the <c>Request</c> property
/// with <c>SetValidator</c>.
/// <para>
/// <b>Every</b> registered validator for the request type runs, not just the first one, which is the
/// same policy the command and query decorators apply to <c>IValidator&lt;TCommand&gt;</c>: a module
/// that authors a validator beside a framework-supplied one expects both sets of rules enforced, and
/// honoring only the first registration would turn the others into dead code whose rules are silently
/// unenforced. FluentValidation unions the failures of the rules placed on one property.
/// </para>
/// <para>
/// Registrations are de-duplicated by runtime type, so a validator class registered twice (a module
/// assembly scanned twice, say) reports each of its failures once rather than in duplicate.
/// </para>
/// <para>
/// This validator is registered automatically by
/// <see cref="DependencyInjection.ScanModuleApplicationServices{TAssemblyMarker}"/> using
/// <c>TryAdd</c> semantics: explicit command validators take precedence.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type that embeds the request.</typeparam>
/// <typeparam name="TRequest">The embedded request type containing the validated fields.</typeparam>
public sealed class CommandRequestValidator<TCommand, TRequest> : AbstractValidator<TCommand>
    where TCommand : ICommandWithRequest<TRequest>
{
    public CommandRequestValidator(IEnumerable<IValidator<TRequest>> requestValidators)
    {
        ArgumentNullException.ThrowIfNull(requestValidators);

        foreach (var validator in requestValidators.DistinctBy(v => v.GetType()))
        {
            RuleFor(c => c.Request).SetValidator(validator);
        }
    }
}
