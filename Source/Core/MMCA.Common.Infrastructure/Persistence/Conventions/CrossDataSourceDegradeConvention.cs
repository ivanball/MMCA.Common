using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Persistence.Conventions;

/// <summary>
/// Model-finalizing convention that automatically degrades relationships whose two ends live in
/// different physical data sources ("database per microservice"):
/// <list type="bullet">
///   <item>The EF relationship and FK constraint are removed — a database cannot enforce an FK
///   into another database. Declared scalar FK columns survive, with a compensating index.</item>
///   <item>CLR navigation members targeting foreign entities are ignored; runtime navigation flows
///   through the existing <c>INavigationPopulator</c> batch-loading machinery instead, and
///   consistency is maintained via integration events.</item>
///   <item>Entity types belonging to another physical source (pulled into the model by EF's
///   relationship discovery, or promoted to explicit by cross-cutting configuration like the
///   soft-delete filters) are removed from this model entirely.</item>
/// </list>
/// Mutations go through the <b>mutable</b> model API rather than convention builders:
/// cross-cutting helpers (soft-delete filters, concurrency tokens) promote every entity type to
/// the Explicit configuration source, which convention-sourced builder calls cannot override.
/// <para>
/// When every entity resolves to the same physical source (the monolith collapse case) nothing is
/// foreign and this convention is a structural no-op — the model is identical to the
/// single-database model.
/// </para>
/// </summary>
/// <param name="contextKey">The physical data source of the context whose model is being built.</param>
/// <param name="registry">Registry mapping entity types to their physical data sources.</param>
public sealed class CrossDataSourceDegradeConvention(
    DataSourceKey contextKey,
    IEntityDataSourceRegistry registry) : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The runtime Model class implements both IConventionModel and IMutableModel; the mutable
        // surface applies changes with explicit precedence, which convention builders cannot.
        var model = (IMutableModel)modelBuilder.Metadata;

        var foreignEntityTypes = model.GetEntityTypes()
            .Where(et => !et.IsOwned() && IsForeign(et.ClrType))
            .ToList();

        if (foreignEntityTypes.Count == 0)
        {
            return;
        }

        var foreignClrTypes = foreignEntityTypes.Select(et => et.ClrType).ToHashSet();
        var localEntityTypes = model.GetEntityTypes()
            .Where(et => !foreignClrTypes.Contains(et.ClrType))
            .ToList();

        // 1) Degrade FKs declared on LOCAL dependents that point at foreign principals
        //    (keep declared scalar FK columns + a compensating index). FKs declared on foreign
        //    dependents disappear together with the foreign entity type in step 3.
        var addCompensatingIndex = contextKey.Engine != DataSource.CosmosDB;
        foreach (var entityType in localEntityTypes)
        {
            foreach (var foreignKey in entityType.GetDeclaredForeignKeys()
                .Where(fk => foreignClrTypes.Contains(fk.PrincipalEntityType.ClrType))
                .ToList())
            {
                DegradeForeignKey(entityType, foreignKey, addCompensatingIndex);
            }
        }

        // 2) Ignore every CLR member on local entities whose (unwrapped) type is foreign —
        //    removed navigations leave unmapped CLR properties of entity type behind, which
        //    model validation would otherwise reject.
        foreach (var entityType in localEntityTypes)
        {
            IgnoreForeignMembers(entityType, foreignClrTypes);
        }

        // 3) Remove the foreign entity types from this model.
        foreach (var foreignEntityType in foreignEntityTypes)
        {
            model.RemoveEntityType(foreignEntityType);
        }
    }

    private bool IsForeign(Type clrType) =>
        clrType.FullName is not null
        && registry.TryGetDataSourceKey(clrType.FullName, out var key)
        && key != contextKey;

    /// <summary>
    /// Removes a cross-source FK (and both its navigations), then re-adds a plain index over the
    /// surviving declared scalar FK columns unless an existing index already covers them as a prefix
    /// (the conventional FK index disappears with the relationship).
    /// <para>
    /// The compensating index is skipped for Cosmos (<paramref name="addCompensatingIndex"/> is
    /// <see langword="false"/>): Cosmos auto-indexes every property and the provider rejects explicit
    /// index definitions, so adding one would fail model validation. This is what makes a
    /// configuration body carrying a cross-source relationship portable to Cosmos without edits.
    /// </para>
    /// </summary>
    private static void DegradeForeignKey(
        IMutableEntityType dependent,
        IMutableForeignKey foreignKey,
        bool addCompensatingIndex)
    {
        var scalarProperties = foreignKey.Properties
            .Where(p => !p.IsShadowProperty())
            .ToList();

        dependent.RemoveForeignKey(foreignKey);

        if (!addCompensatingIndex || scalarProperties.Count == 0)
        {
            return;
        }

        // Eagerly drop the index ForeignKeyIndexConvention created for the removed FK — its
        // deferred event processing would otherwise remove it AFTER our coverage check, leaving
        // the column unindexed.
        if (dependent.FindIndex(scalarProperties) is { } autoIndex
            && ((IConventionIndex)autoIndex).GetConfigurationSource() == ConfigurationSource.Convention)
        {
            dependent.RemoveIndex(autoIndex);
        }

        if (HasCoveringIndex(dependent, scalarProperties))
        {
            return;
        }

        dependent.AddIndex(scalarProperties);
    }

    private static bool HasCoveringIndex(IMutableEntityType entityType, List<IMutableProperty> properties) =>
        entityType.GetIndexes().Any(index =>
            index.Properties.Count >= properties.Count
            && properties.Select(p => p.Name)
                .SequenceEqual(index.Properties.Take(properties.Count).Select(p => p.Name), StringComparer.Ordinal));

    /// <summary>
    /// Ignores skip navigations and unmapped CLR properties whose target type is a foreign entity
    /// (covers single references and collections of foreign entities). Regular navigations were
    /// already removed together with their FKs.
    /// </summary>
    private static void IgnoreForeignMembers(IMutableEntityType entityType, HashSet<Type> foreignClrTypes)
    {
        foreach (var skipNavigation in entityType.GetDeclaredSkipNavigations()
            .Where(n => foreignClrTypes.Contains(n.TargetEntityType.ClrType))
            .ToList())
        {
            entityType.RemoveSkipNavigation(skipNavigation);
        }

        foreach (var property in entityType.ClrType.GetProperties()
            .Where(p => foreignClrTypes.Contains(UnwrapCollectionElementType(p.PropertyType))))
        {
            entityType.AddIgnored(property.Name);
        }
    }

    /// <summary>
    /// Unwraps generic single-argument collection types (e.g. <c>List&lt;T&gt;</c>,
    /// <c>ICollection&lt;T&gt;</c>) to the element type for foreign-entity detection.
    /// </summary>
    private static Type UnwrapCollectionElementType(Type propertyType) =>
        propertyType.IsGenericType && propertyType.GenericTypeArguments.Length == 1
            ? propertyType.GenericTypeArguments[0]
            : propertyType;
}
