using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.Conventions;

/// <summary>
/// Model-finalizing convention that makes <see cref="DeleteBehavior.Restrict"/> the default for every
/// relationship nobody configured, and records on each foreign key whether its delete behavior was
/// chosen or inherited.
/// <para>
/// EF's own default is the opposite posture: a required relationship cascades, so deleting a parent
/// silently deletes its children in the database, below the aggregate's invariants, below the
/// soft-delete filter and below the domain events the framework raises. That is a destructive default
/// nobody wrote down. Restricting by default inverts it: a delete that would orphan rows fails loudly,
/// and a genuine cascade becomes a decision recorded in the entity configuration with
/// <c>.OnDelete(DeleteBehavior.Cascade)</c>.
/// </para>
/// <para>
/// <b>What is left alone.</b> An ownership foreign key keeps its cascade: an owned type has no
/// identity apart from its owner and EF requires the cascade, so the convention only stamps it as
/// <see cref="OwnershipSource"/> and moves on. A delete behavior anybody configured (fluent
/// <c>OnDelete</c> or the <c>[DeleteBehavior]</c> attribute) is kept exactly as configured and stamped
/// <see cref="ExplicitSource"/>: this convention never overrides a decision.
/// </para>
/// <para>
/// <b>The annotation.</b> Every foreign key carries <c>MMCA:DeleteBehaviorSource</c>, so a finalized
/// model can be audited without re-running the convention and without a convention-model walk:
/// <c>DeleteBehaviorConventionTestsBase</c> reads exactly this annotation to assert that every
/// cascading relationship was opted into on purpose.
/// </para>
/// <para>
/// A no-op for Cosmos, which has no foreign key constraints to restrict: the provider models parent
/// and child as one document or as independent documents, and stamping a delete-behavior decision on
/// a model that cannot enforce one would only make the audit lie.
/// </para>
/// </summary>
/// <param name="engine">The engine of the context whose model is being built.</param>
public sealed class RestrictDeleteByDefaultConvention(DataSource engine) : IModelFinalizingConvention
{
    /// <summary>
    /// Name of the annotation stamped on every foreign key, carrying one of
    /// <see cref="ExplicitSource"/>, <see cref="ConventionSource"/> or <see cref="OwnershipSource"/>.
    /// </summary>
    public const string DeleteBehaviorSourceAnnotation = "MMCA:DeleteBehaviorSource";

    /// <summary>Annotation value for a delete behavior an entity configuration chose.</summary>
    public const string ExplicitSource = "Explicit";

    /// <summary>Annotation value for a delete behavior this convention supplied.</summary>
    public const string ConventionSource = "Convention";

    /// <summary>Annotation value for an ownership foreign key, whose cascade EF owns.</summary>
    public const string OwnershipSource = "Ownership";

    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        if (engine == DataSource.CosmosDB)
        {
            return;
        }

        // Materialized before the walk: the cross-source convention runs first and may already have
        // removed relationships, and nothing here should observe a collection it is mutating.
        var foreignKeys = modelBuilder.Metadata.GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredForeignKeys())
            .ToList();

        foreach (var foreignKey in foreignKeys)
        {
            Apply(foreignKey);
        }
    }

    /// <summary>
    /// Stamps one foreign key with the source of its delete behavior, restricting it first when
    /// nobody configured one.
    /// </summary>
    /// <param name="foreignKey">The foreign key to stamp.</param>
    private static void Apply(IConventionForeignKey foreignKey)
    {
        if (foreignKey.IsOwnership)
        {
            foreignKey.SetAnnotation(DeleteBehaviorSourceAnnotation, OwnershipSource);
            return;
        }

        // Null means nobody ever set one; Convention means an EF convention did (the required-FK rule
        // that produces the cascading default). Both are "inherited", and both are ours to replace.
        var configurationSource = foreignKey.GetDeleteBehaviorConfigurationSource();
        if (configurationSource is null or ConfigurationSource.Convention)
        {
            foreignKey.SetDeleteBehavior(DeleteBehavior.Restrict);
            foreignKey.SetAnnotation(DeleteBehaviorSourceAnnotation, ConventionSource);
            return;
        }

        foreignKey.SetAnnotation(DeleteBehaviorSourceAnnotation, ExplicitSource);
    }
}
