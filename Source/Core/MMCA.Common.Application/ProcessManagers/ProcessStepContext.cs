using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// What a process step receives from <see cref="ProcessDomainEventHandlerBase{TDomainEvent, TProcess, TProcessId}"/>:
/// the scope the base opened for this event, its unit of work (inside the open transaction), and the
/// timeout scheduler when the host called <c>AddProcessManagers()</c>.
/// </summary>
public sealed class ProcessStepContext
{
    /// <summary>Initializes a new instance of the <see cref="ProcessStepContext"/> class.</summary>
    /// <param name="services">The event's scope provider.</param>
    /// <param name="unitOfWork">The scope's unit of work.</param>
    public ProcessStepContext(IServiceProvider services, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        Services = services;
        UnitOfWork = unitOfWork;
        Timeouts = services.GetService<IProcessTimeoutScheduler>();
    }

    /// <summary>Gets the event's scope provider, for the step's other services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the scope's unit of work. The step owns every save.</summary>
    public IUnitOfWork UnitOfWork { get; }

    /// <summary>Gets the timeout scheduler, or <see langword="null"/> when the host never called <c>AddProcessManagers()</c>.</summary>
    public IProcessTimeoutScheduler? Timeouts { get; }
}
