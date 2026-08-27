using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Opt-in marker a query request DTO can implement to declare the result type its handler
/// produces, so the request and its <see cref="IQueryHandler{TQuery, TResult}"/> can be paired by a
/// compile-time or fitness check instead of by convention alone.
/// </summary>
/// <typeparam name="TResult">The result type the query's handler returns.</typeparam>
/// <remarks>
/// <para>
/// This marker is <b>purely additive</b>, exactly like <see cref="ICommand{TResult}"/>: handlers
/// keep implementing <see cref="IQueryHandler{TQuery, TResult}"/>, nothing in the registration or
/// decorator pipeline reads it, and a query that does not implement it behaves identically.
/// </para>
/// <para>
/// <see cref="CqrsContractInspector"/> consumes it: a fitness test can assert that no handler
/// contradicts the result type its query declares.
/// </para>
/// </remarks>
[SuppressMessage(
    "Major Code Smell",
    "S2326:Unused type parameters should be removed",
    Justification = "TResult is a phantom type parameter by design, exactly as on ICommand<TResult>: the marker exists to carry the declared result type on the request for CqrsContractInspector and consumer-side generic constraints to read.")]
public interface IQuery<TResult>;
