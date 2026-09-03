using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.UseCases.Contracts;

/// <summary>
/// Opt-in marker a command request DTO can implement to declare the result type its handler
/// produces, so the request and its <see cref="ICommandHandler{TCommand, TResult}"/> can be paired
/// by a compile-time or fitness check instead of by convention alone.
/// </summary>
/// <typeparam name="TResult">
/// The result type the command's handler returns (typically <c>Result</c> or <c>Result&lt;T&gt;</c>).
/// </typeparam>
/// <remarks>
/// <para>
/// This marker is <b>purely additive</b>. Handlers keep implementing
/// <see cref="ICommandHandler{TCommand, TResult}"/> exactly as before, nothing in the registration
/// or decorator pipeline reads it, and a command that does not implement it behaves identically.
/// Adopt it per command, at whatever pace suits the module.
/// </para>
/// <para>
/// The pay-off is <see cref="CqrsContractInspector"/>: once a command declares its result type,
/// a fitness test can assert that every handler written for it returns that same type, turning a
/// silent "handler returns <c>Result&lt;int&gt;</c> but every caller expects <c>Result&lt;Guid&gt;</c>"
/// drift into a failing test.
/// </para>
/// </remarks>
[SuppressMessage(
    "Major Code Smell",
    "S2326:Unused type parameters should be removed",
    Justification = "TResult is a phantom type parameter by design: the whole point of the marker is to carry the declared result type on the request, where CqrsContractInspector (and any consumer-side generic constraint) can read it. Consuming it in a member would force every command DTO to implement a method it has no business owning.")]
public interface ICommand<TResult>;
