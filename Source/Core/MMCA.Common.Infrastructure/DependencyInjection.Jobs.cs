using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Enables the recurring job scheduler: binds the <c>Scheduler</c> settings section and
        /// registers the background runner that executes registered
        /// <see cref="IScheduledJob"/>s on their cron schedules.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the <c>Scheduler</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Call this <b>once</b> per host, and <c>AddScheduledJob&lt;TJob&gt;</c> any number of
        /// times. The two are order-free: a module may register its jobs before the host enables the
        /// scheduler, or after. Calling this more than once is harmless, since the runner is
        /// registered through <c>TryAddEnumerable</c> and would otherwise run twice in one process.
        /// </para>
        /// <para>
        /// Registering the scheduler is not the same as turning it on. The runner and the
        /// <c>ScheduledJobs</c> table both stay inert until <c>Scheduler:Enabled</c> is true, so a
        /// host can ship the registration and enable it per environment.
        /// </para>
        /// </remarks>
        public IServiceCollection AddScheduledJobs(IConfiguration configuration)
        {
            services.AddOptions<SchedulerSettings>()
                .Bind(configuration.GetSection(SchedulerSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // TryAddEnumerable, not AddHostedService: the latter appends a descriptor per call, so a
            // host (or two modules) calling this twice would run two runners in one process, and both
            // would race for the same job rows.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, Scheduling.ScheduledJobRunner>());

            return services;
        }

        /// <summary>
        /// Registers one <see cref="IScheduledJob"/> implementation with the scheduler.
        /// </summary>
        /// <typeparam name="TJob">The job implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Jobs accumulate: each module calls this for the jobs it owns and every registration is
        /// added to the same <c>IEnumerable&lt;IScheduledJob&gt;</c>, exactly like the permission
        /// registry's accumulate-across-modules idiom. Registration order does not matter, and the
        /// same type registered twice is added once.
        /// </para>
        /// <para>
        /// The job is <b>scoped</b>: the runner resolves it in a fresh scope per execution, so it may
        /// take scoped dependencies (unit of work, repositories, handlers) and must hold no state
        /// between runs. The concrete type is registered too, so a host can resolve it directly (a
        /// test, or an admin endpoint that triggers the job on demand).
        /// </para>
        /// </remarks>
        public IServiceCollection AddScheduledJob<TJob>()
            where TJob : class, IScheduledJob
        {
            services.TryAddScoped<TJob>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IScheduledJob, TJob>());
            return services;
        }

        /// <summary>
        /// Enables the entity change-history trail: binds the <c>AuditTrail</c> settings section,
        /// registers the interceptor that records changes to <c>IAuditedEntity</c> entities, the read
        /// surface (<see cref="IAuditTrailReader"/>), and the retention job.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the <c>AuditTrail</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Call this once per host. Registering the trail is not the same as turning it on: the
        /// interceptor and the <c>AuditTrailEntries</c> table both stay inert until
        /// <c>AuditTrail:Enabled</c> is true, so a host can ship the registration and enable it per
        /// environment. A host that never calls this keeps exactly the model and the save pipeline it
        /// had before the trail shipped, because <c>ApplicationDbContext</c> resolves the interceptor
        /// with <c>GetService</c> and finds nothing.
        /// </para>
        /// <para>
        /// <b>Retention needs the scheduler.</b> This registers <c>AuditTrailCleanupJob</c>, but a
        /// job only runs when the host ALSO calls <c>AddScheduledJobs(configuration)</c> and sets
        /// <c>Scheduler:Enabled</c>. Without the scheduler the trail still records every change and
        /// nothing is ever purged: pruning the table is then the operator's job, and
        /// <c>AuditTrail:RetentionDays</c> is inert.
        /// </para>
        /// <para>
        /// Marking entities is the other half: an entity records nothing until it carries
        /// <c>IAuditedEntity</c>. That is deliberate, and it is where the write volume is decided.
        /// </para>
        /// </remarks>
        public IServiceCollection AddAuditTrail(IConfiguration configuration)
        {
            services.AddOptions<AuditTrailSettings>()
                .Bind(configuration.GetSection(AuditTrailSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Singleton for the same reason as the other two save interceptors: stateless, with any
            // per-save state held in a ConditionalWeakTable keyed by context.
            services.TryAddSingleton<Persistence.AuditTrail.AuditTrailSaveChangesInterceptor>();

            services.TryAddScoped<IAuditTrailReader, Persistence.AuditTrail.AuditTrailReader>();

            // The framework's own scheduled job. Registering it here rather than in AddScheduledJobs
            // keeps the two features independent: the trail can be enabled without the scheduler, and
            // the scheduler without the trail.
            services.AddScheduledJob<Persistence.AuditTrail.AuditTrailCleanupJob>();

            return services;
        }
    }

    /// <summary>
    /// Registers the internal-command job queue: settings, the scheduler, the operator surface,
    /// and (when <c>InternalCommands:Enabled</c>) the processor and its retention sweep.
    /// </summary>
    /// <remarks>
    /// The posture mirrors the outbox deliberately. The <c>InternalCommands</c> table is mapped
    /// into every relational source unconditionally, so the flag never implies a migration; it
    /// only decides whether THIS host drains the queue. A host with the flag off still writes
    /// rows through <c>IInternalCommandScheduler</c>, and a startup notice says so once.
    /// </remarks>
    /// <param name="services">The collection being configured.</param>
    /// <param name="configuration">Application configuration for binding the section.</param>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddInfrastructure inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static void AddInternalCommands(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<Persistence.InternalCommands.Administration.InternalCommandsSettings>()
            .Bind(configuration.GetSection(
                Persistence.InternalCommands.Administration.InternalCommandsSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<
            Persistence.InternalCommands.Processing.IInternalCommandSignal,
            Persistence.InternalCommands.Processing.InternalCommandSignal>();

        // Scoped: the row is written on the SAME context factory the calling handler's
        // repositories use, which is what makes scheduling atomic with the aggregate change.
        services.TryAddScoped<Application.InternalCommands.IInternalCommandScheduler,
            Persistence.InternalCommands.InternalCommandScheduler>();

        // Operator surface over the same queue tables: count, list, requeue and purge. Scoped,
        // because it creates one child scope per data source it visits and holds no state.
        services.TryAddScoped<Application.InternalCommands.IInternalCommandAdministration,
            Persistence.InternalCommands.Administration.InternalCommandAdministration>();

        var internalCommandSettings = configuration
            .GetSection(Persistence.InternalCommands.Administration.InternalCommandsSettings.SectionName)
            .Get<Persistence.InternalCommands.Administration.InternalCommandsSettings>()
            ?? new Persistence.InternalCommands.Administration.InternalCommandsSettings();

        if (internalCommandSettings.Enabled)
        {
            services.AddHostedService<Persistence.InternalCommands.Processing.InternalCommandProcessor>();
            services.AddHostedService<Persistence.InternalCommands.Administration.InternalCommandCleanupService>();
        }
        else
        {
            services.AddHostedService<
                Persistence.InternalCommands.Administration.InternalCommandsDisabledNoticeService>();
        }
    }
}
