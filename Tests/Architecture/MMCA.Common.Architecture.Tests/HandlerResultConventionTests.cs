using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Handler TResult convention for the framework's own Application layer (the Notifications
/// command/query handlers), driven by the shared rule library
/// (<see cref="HandlerResultConventionTestsBase"/>) over <see cref="CommonArchitectureMap"/>:
/// every concrete handler's TResult must be Result or Result&lt;T&gt;, the constraint the decorator
/// pipeline otherwise only enforces at runtime via ResultFailureFactory's InvalidOperationException.
/// </summary>
public sealed class HandlerResultConventionTests : HandlerResultConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
