using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers one <see cref="IUserDataExportSection"/> implementation as a contributor to the
        /// data-subject export.
        /// </summary>
        /// <typeparam name="TSection">The section implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Sections accumulate: each module calls this for the data it owns and every registration is
        /// added to the same <c>IEnumerable&lt;IUserDataExportSection&gt;</c> the export handler fans
        /// out over, exactly like the scheduled-job and permission-registry idioms. The same type
        /// registered twice is added once, and registration order is the order the sections appear in
        /// the export document.
        /// </para>
        /// <para>
        /// The section is <b>scoped</b>: it runs inside the request's unit of work, so it may take
        /// scoped dependencies (repositories, gRPC clients, handlers).
        /// </para>
        /// <para>
        /// The export handler itself needs no registration here. Apps subclass
        /// <c>ExportUserDataHandlerBase&lt;TUser, TQuery&gt;</c> in their own Application assembly,
        /// and <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/> picks the concrete
        /// subclass up as an <c>IQueryHandler</c> like any other handler.
        /// </para>
        /// </remarks>
        public IServiceCollection AddUserDataExportSection<TSection>()
            where TSection : class, IUserDataExportSection
        {
            services.TryAddScoped<TSection>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataExportSection, TSection>());
            return services;
        }

        /// <summary>
        /// Registers one <see cref="IEventUpcaster{TSource, TTarget}"/> implementation, so a message
        /// still published (or still queued) as the retired contract <typeparamref name="TSource"/> is
        /// delivered to the handlers written against its successor <typeparamref name="TTarget"/>.
        /// </summary>
        /// <typeparam name="TSource">The retired event contract.</typeparam>
        /// <typeparam name="TTarget">The successor event contract. Must declare a higher <c>SchemaVersion</c>.</typeparam>
        /// <typeparam name="TUpcaster">The upcaster implementation.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// ADR-010 makes a breaking event-shape change a NEW event type plus a consumer-side upcaster;
        /// this is the registration extension point for that policy (ADR-090). Registrations accumulate
        /// through <c>TryAddEnumerable</c>, the same idiom as <see cref="AddUserDataExportSection{TSection}"/>
        /// and the scheduled-job registry, and the same type registered twice is added once.
        /// </para>
        /// <para>
        /// <b>Singleton</b>, because upcasters are pure functions over an event instance, matching the
        /// handler lifetimes they feed. <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>
        /// are named explicitly so the compiler checks the shape at the registration site instead of
        /// leaving a mismatch to fail at runtime.
        /// </para>
        /// <para>
        /// Chains compose: registering V1 to V2 and V2 to V3 delivers a V1 message to the V3 handler.
        /// Registering two upcasters for one source, mapping a type onto itself, or forming a cycle
        /// fails the host at startup with an exception naming the offenders.
        /// </para>
        /// <para>
        /// A monolith host needs only this call. A host that also consumes the retired contract over a
        /// broker adds <c>x.RegisterUpcastedIntegrationEventConsumer&lt;TSource&gt;()</c> beside its
        /// <c>x.RegisterIntegrationEventConsumer&lt;TTarget&gt;()</c>. Once every producer publishes the
        /// successor and the queues have drained, delete the upcaster, both registrations and
        /// eventually the retired type.
        /// </para>
        /// </remarks>
        public IServiceCollection AddEventUpcaster<TSource, TTarget, TUpcaster>()
            where TSource : class, IIntegrationEvent
            where TTarget : class, IIntegrationEvent
            where TUpcaster : class, IEventUpcaster<TSource, TTarget>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventUpcaster, TUpcaster>());
            return services;
        }
    }
}
