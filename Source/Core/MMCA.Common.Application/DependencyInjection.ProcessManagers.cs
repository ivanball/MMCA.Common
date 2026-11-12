using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.ProcessManagers;

namespace MMCA.Common.Application;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the process-manager primitive's one service: the scoped
        /// <see cref="IProcessTimeoutScheduler"/>, plus <see cref="TimeProvider.System"/> when the host
        /// registered no clock.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// It registers no handler, so it may be called inside or outside the sealed application
        /// pipeline, and a second call adds nothing. Timeout handlers and step handlers are consumer
        /// subclasses of <see cref="ProcessTimeoutHandlerBase{TCommand, TProcess, TProcessId}"/> and
        /// <see cref="ProcessDomainEventHandlerBase{TDomainEvent, TProcess, TProcessId}"/>, found by the
        /// module scan. <c>AddApplication()</c> registers none of this.
        /// </remarks>
        public IServiceCollection AddProcessManagers()
        {
            services.TryAddScoped<IProcessTimeoutScheduler, ProcessTimeoutScheduler>();
            services.TryAddSingleton(TimeProvider.System);
            return services;
        }
    }
}
