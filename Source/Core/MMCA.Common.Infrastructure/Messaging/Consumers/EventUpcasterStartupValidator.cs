using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// Forces the event-upcaster registration graph to be validated at host start rather than on the
/// first message. <see cref="IEventUpcasterRegistry"/>'s implementation validates in its constructor
/// (duplicate source, a type mapped onto itself, a cycle), so simply resolving it is the check: a
/// misconfigured host fails to start with an exception naming the offenders, instead of dead-lettering
/// events hours later.
/// <para>
/// Registered by <c>AddInfrastructure</c> through <c>TryAddEnumerable</c>, so several modules calling
/// it do not run the validation several times. A host with no upcasters resolves an empty registry and
/// this costs one no-op call (ADR-090).
/// </para>
/// </summary>
/// <param name="upcasters">The registry whose construction performs the validation.</param>
internal sealed class EventUpcasterStartupValidator(IEventUpcasterRegistry upcasters) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reading one member keeps the injected registry load-bearing: the work happened in its
        // constructor, and this call is what makes the dependency impossible to elide.
        _ = upcasters.ResolveTerminalType(typeof(IIntegrationEvent));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
