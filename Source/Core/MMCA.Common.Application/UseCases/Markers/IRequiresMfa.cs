namespace MMCA.Common.Application.UseCases.Markers;

/// <summary>
/// Marker interface for commands and queries that may only run when the caller's token was minted
/// after a second authentication factor was presented, that is when the principal carries the
/// <c>mfa</c> claim.
/// <para>
/// The <see cref="Decorators.AuthorizationCommandDecorator{TCommand,TResult}"/> and
/// <see cref="Decorators.AuthorizationQueryDecorator{TQuery,TResult}"/> check it in the same pass
/// they check <see cref="IRequiresPermission"/>, short-circuiting with a
/// <see cref="Common.Shared.Abstractions.ErrorType.Forbidden"/> failure when the claim is absent.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Opting in is per request type and orthogonal to <see cref="IRequiresPermission"/>: a use case may
/// carry either, both, or neither. Both checks must pass, and the multi-factor one runs second so a
/// caller who lacks the capability entirely is not told which use cases are additionally
/// step-up-protected.
/// </para>
/// <para>
/// <b>Absence denies.</b> There is no "the account has no second factor so let it through" branch. A
/// use case marked with this interface is one where step-up is the point (rotating a payment
/// credential, exporting an operator's data), so an account that cannot present a second factor
/// cannot reach it, and the fix is enrolling rather than a fallback.
/// </para>
/// </remarks>
public interface IRequiresMfa;
