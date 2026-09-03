namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Idempotency convention gate: every POST action in the repo's API layer states, in code, whether a
/// retried request replays the original response (<c>[Idempotent]</c>) or deliberately does not
/// (<c>[NonIdempotent("why")]</c>). Subclass it in a repo whose map declares an
/// <see cref="Layer.Api"/> assembly; a repo with no API layer has nothing to gate and simply does not
/// subclass.
/// </summary>
public abstract class IdempotencyConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void PostActions_ShouldDeclare_IdempotencyIntent() =>
        ArchitectureRules.PostActionsDeclareIdempotencyIntent(Map);
}
