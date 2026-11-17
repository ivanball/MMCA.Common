using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Trailing-CancellationToken convention for the MMCA.Common Application and Infrastructure packages,
/// driven by the shared rule library (<see cref="CancellationTokenConventionTestsBase"/>) over
/// <see cref="CommonArchitectureMap"/>. The two <c>NotificationHub</c> SignalR methods are exempted by the
/// base itself, so this subclass, like every consumer's, adds no exemption of its own.
/// </summary>
public sealed class CancellationTokenConventionTests : CancellationTokenConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
