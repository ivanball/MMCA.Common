namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Marker interface for commands that embed a request DTO as a <c>Request</c> property.
/// <para>
/// Commands implementing this interface get <b>automatic validation</b>: the framework
/// registers a <see cref="Validation.CommandRequestValidator{TCommand,TRequest}"/> that
/// delegates to <c>IValidator&lt;TRequest&gt;</c> via FluentValidation's <c>SetValidator</c>.
/// Registration uses <c>TryAdd</c> semantics, so an explicit <c>IValidator&lt;TCommand&gt;</c>
/// always takes precedence.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The type of the embedded request payload.</typeparam>
public interface ICommandWithRequest<out TRequest>
{
    /// <summary>The embedded request payload (typically deserialized from the HTTP body).</summary>
    TRequest Request { get; }
}
