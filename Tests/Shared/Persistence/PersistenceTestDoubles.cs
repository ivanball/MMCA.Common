using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Tests.Shared.Persistence;

/// <summary>
/// Points entity configuration discovery at the test assembly this file is compiled into, so the
/// engine tiers map only their own fixture entities.
/// <para>
/// Linked as source into the SQL Server and PostgreSQL provider test projects (see the
/// <c>Compile Include</c> in each project file), so every project compiles its own internal copy and
/// <see cref="FixedAssemblyProvider"/>'s assembly is that project's test assembly.
/// </para>
/// </summary>
internal sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
{
    public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
        [typeof(FixedAssemblyProvider).Assembly];
}

/// <summary>
/// Stands in for the in-process dispatcher: the outbox routing only needs the call to succeed,
/// and a handler of its own would test the dispatcher rather than the provider.
/// </summary>
internal sealed class RecordingDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
