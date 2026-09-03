using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// The framework's own API layer honours the idempotency convention gate
/// (<see cref="IdempotencyConventionTestsBase"/>) over <see cref="CommonArchitectureMap"/>: every
/// POST it ships either replays on an <c>Idempotency-Key</c> or says in code why it must not.
/// </summary>
public sealed class IdempotencyConventionTests : IdempotencyConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
