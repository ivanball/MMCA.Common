using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Application.Validation;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the framework's generic write side for one aggregate: the create, update and
        /// delete handlers, each closed over that aggregate's own types, plus the validator bridge
        /// for the update command. One call replaces the three hand-written handler classes a
        /// straightforward CRUD aggregate would otherwise need.
        /// </summary>
        /// <typeparam name="TEntity">The aggregate root.</typeparam>
        /// <typeparam name="TEntityDTO">The DTO the create and update handlers return.</typeparam>
        /// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
        /// <typeparam name="TCreateRequest">The create request, which is also the create command.</typeparam>
        /// <typeparam name="TUpdateRequest">The update request carried by <c>UpdateEntityCommand</c>.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <b>What the aggregate still owns.</b> Three collaborators, all picked up by
        /// <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/>: an
        /// <see cref="IEntityRequestMapper{TEntity, TCreateRequest, TIdentifierType}"/> that runs the
        /// entity factory, an <see cref="IEntityUpdateApplier{TEntity, TUpdateRequest, TIdentifierType}"/>
        /// that calls the aggregate's guarded mutation methods, and an
        /// <see cref="IEntityDTOMapper{TEntity, TEntityDTO, TIdentifierType}"/>. The invariants and the
        /// domain events stay in the aggregate; nothing here writes a property.
        /// </para>
        /// <para>
        /// <b>Registrations are closed, not open-generic</b>, and use <c>TryAdd</c>. Closed, because
        /// Scrutor's <c>TryDecorate</c> wraps concrete service types: an open
        /// <c>ICommandHandler&lt;,&gt;</c> registration would resolve completely undecorated and
        /// <c>VerifyDecoratorPipeline()</c> could not see it. <c>TryAdd</c>, so an aggregate that
        /// outgrows one of the three (a create that needs a retry loop, a delete that has to load its
        /// children first) registers its own handler for that verb before this call and keeps the
        /// generic pair for the other two.
        /// </para>
        /// <para>
        /// <b>The update command's validator bridge is registered too</b>, exactly as
        /// <see cref="AddEntityUpdateVerb{TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier}"/>
        /// does for a verb: a <see cref="CommandRequestValidator{TCommand, TRequest}"/> closed over
        /// <c>UpdateEntityCommand</c> and <typeparamref name="TUpdateRequest"/>, so the rules a module
        /// writes for the request run against the command the generic controller constructs. The
        /// command is a closed generic built at registration time, so the module scan's reflection
        /// bridge cannot see it. <c>TryAdd</c> semantics, like the scan: an explicitly registered
        /// <c>IValidator</c> for that command wins.
        /// </para>
        /// <para>
        /// Call it where a module registers its handlers, which means before
        /// <c>AddApplicationDecorators()</c>: inside the module's own <c>Register</c>, or inside the
        /// <c>AddMmcaApplicationPipeline(...)</c> callback.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddEntityCrud&lt;Ticket, TicketDTO, TicketIdentifierType, TicketCreateRequest, TicketUpdateRequest&gt;();
        /// </code>
        /// </example>
        /// </remarks>
        public IServiceCollection AddEntityCrud<TEntity, TEntityDTO, TIdentifierType, TCreateRequest, TUpdateRequest>()
            where TEntity : AuditableAggregateRootEntity<TIdentifierType>
            where TEntityDTO : class, IBaseDTO<TIdentifierType>
            where TIdentifierType : notnull
            where TCreateRequest : class, ICreateRequest
        {
            ThrowIfPipelineSealed(services, nameof(AddEntityCrud));

            services.TryAddScoped<
                ICommandHandler<TCreateRequest, Result<TEntityDTO>>,
                CreateEntityHandler<TCreateRequest, TEntity, TIdentifierType, TEntityDTO>>();

            services.TryAddScoped<
                ICommandHandler<UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>, Result<TEntityDTO>>,
                UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>>();

            services.TryAddScoped<
                ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result>,
                DeleteEntityHandler<TEntity, TIdentifierType>>();

            services.AddCommandRequestValidator<
                UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>,
                TUpdateRequest>();

            return services;
        }

        /// <summary>
        /// Registers one <b>verb</b> of the generic update path: the handler closed over
        /// <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType, TApplier}"/> and
        /// the validator bridge for that command. Call it once per verb when an aggregate has
        /// several mutations that share a request shape.
        /// </summary>
        /// <typeparam name="TEntity">The aggregate root.</typeparam>
        /// <typeparam name="TEntityDTO">The DTO the handler returns.</typeparam>
        /// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
        /// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
        /// <typeparam name="TApplier">The applier serving this verb, which also discriminates it.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The wire shape does not change: the route and the request DTO stay what they were, and the
        /// controller action constructs the verb's own closed command. Two verbs over one request DTO
        /// become two commands, two handlers and two appliers, with nothing duplicated but the
        /// registration line.
        /// </para>
        /// <para>
        /// The applier is resolved by its concrete type, which the module scan registers alongside its
        /// interfaces. Registration uses <c>TryAdd</c>, like <c>AddEntityCrud</c>, so a verb
        /// that later outgrows the generic handler registers its own first.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddEntityUpdateVerb&lt;InventoryItem, InventoryItemDTO, ProductVariantIdentifierType, InventoryItemAdjustRequest, IncreaseInventoryApplier&gt;();
        /// services.AddEntityUpdateVerb&lt;InventoryItem, InventoryItemDTO, ProductVariantIdentifierType, InventoryItemAdjustRequest, DecreaseInventoryApplier&gt;();
        /// </code>
        /// </example>
        /// </remarks>
        public IServiceCollection AddEntityUpdateVerb<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier>()
            where TEntity : AuditableAggregateRootEntity<TIdentifierType>
            where TEntityDTO : class, IBaseDTO<TIdentifierType>
            where TIdentifierType : notnull
            where TApplier : class, IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType>
        {
            ThrowIfPipelineSealed(services, nameof(AddEntityUpdateVerb));

            services.TryAddScoped<
                ICommandHandler<
                    UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier>,
                    Result<TEntityDTO>>,
                UpdateEntityHandler<TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier>>();

            services.AddCommandRequestValidator<
                UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier>,
                TUpdateRequest>();

            return services;
        }

        /// <summary>
        /// Registers the generic update path for a <b>derived</b> command: a command that inherits
        /// <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType}"/> and adds state
        /// beside the request (a route-derived id, a server-decided flag, a second concurrency token),
        /// served by an
        /// <see cref="IEntityUpdateCommandApplier{TEntity, TUpdateRequest, TIdentifierType, TCommand}"/>
        /// that sees the whole command.
        /// </summary>
        /// <typeparam name="TCommand">The derived command type.</typeparam>
        /// <typeparam name="TEntity">The aggregate root.</typeparam>
        /// <typeparam name="TEntityDTO">The DTO the handler returns.</typeparam>
        /// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
        /// <typeparam name="TUpdateRequest">The update request the command carries.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddApplicationDecorators()</c> has already run on this collection, so handlers registered
        /// now would never be wrapped.
        /// </exception>
        /// <remarks>
        /// <para>
        /// A derived command declared in a module assembly already reaches the module scan's validator
        /// bridge on its own; the bridge is registered here too, with <c>TryAdd</c>, so the
        /// registration is complete whether or not the command lives in a scanned assembly.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddEntityUpdate&lt;UpdateSpeakerCommand, Speaker, SpeakerDTO, SpeakerIdentifierType, SpeakerUpdateRequest&gt;();
        /// </code>
        /// </example>
        /// </remarks>
        public IServiceCollection AddEntityUpdate<TCommand, TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>()
            where TCommand : UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>
            where TEntity : AuditableAggregateRootEntity<TIdentifierType>
            where TEntityDTO : class, IBaseDTO<TIdentifierType>
            where TIdentifierType : notnull
        {
            ThrowIfPipelineSealed(services, nameof(AddEntityUpdate));

            services.TryAddScoped<
                ICommandHandler<TCommand, Result<TEntityDTO>>,
                UpdateEntityCommandHandler<TCommand, TEntity, TEntityDTO, TIdentifierType, TUpdateRequest>>();

            services.AddCommandRequestValidator<TCommand, TUpdateRequest>();

            return services;
        }

        /// <summary>
        /// Bridges one command to its request's validators, registering a
        /// <see cref="CommandRequestValidator{TCommand, TRequest}"/> as the command's
        /// <c>IValidator</c>. The explicit form of what
        /// <see cref="ScanModuleApplicationServices{TAssemblyMarker}"/> does by reflection, for a
        /// command the scan cannot see: a closed generic constructed at registration time rather than
        /// declared in the scanned assembly.
        /// </summary>
        /// <typeparam name="TCommand">The command carrying the request.</typeparam>
        /// <typeparam name="TRequest">The embedded request the module writes rules for.</typeparam>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <c>TryAdd</c> semantics, exactly like the scan: an explicit <c>IValidator&lt;TCommand&gt;</c>
        /// registered first always wins. Registering it without an <c>IValidator&lt;TRequest&gt;</c> in
        /// the container is harmless: the bridge adds no rule when it finds no request validator.
        /// </remarks>
        public IServiceCollection AddCommandRequestValidator<TCommand, TRequest>()
            where TCommand : ICommandWithRequest<TRequest>
        {
            services.TryAddTransient<IValidator<TCommand>, CommandRequestValidator<TCommand, TRequest>>();

            return services;
        }
    }
}
