namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Declares that a POST action is deliberately OUTSIDE the idempotency-key contract, and why.
/// It is a documentation marker, not a filter: it changes no runtime behaviour and attaches no
/// pipeline stage. Its only consumer is the <c>PostActionsDeclareIdempotencyIntent</c> fitness
/// function, which requires every POST action to carry either <see cref="IdempotentAttribute"/> or
/// this attribute, so "no idempotency here" is always a recorded decision rather than an omission
/// nobody noticed.
/// </summary>
/// <remarks>
/// The cases that earn this attribute are the ones where replaying a stored response would be
/// actively wrong rather than merely unhelpful: token issuance and revocation, single-use code
/// exchange, and anything else whose response is only valid for the call that produced it. Reach for
/// <see cref="IdempotentAttribute"/> everywhere else, since the filter no-ops for a request that
/// carries no <c>Idempotency-Key</c> header and therefore costs an existing client nothing.
/// </remarks>
/// <param name="justification">
/// Why this action must not replay a cached response. Required, and meant to be read by the next
/// person who wonders whether the omission was intentional.
/// </param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NonIdempotentAttribute(string justification) : Attribute
{
    /// <summary>
    /// Gets the reason this action stays outside the idempotency-key contract.
    /// </summary>
    public string Justification { get; } = justification;
}
