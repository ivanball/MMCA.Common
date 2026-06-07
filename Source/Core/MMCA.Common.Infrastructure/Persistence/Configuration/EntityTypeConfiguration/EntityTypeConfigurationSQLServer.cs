using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// SQL Server entity configuration base. Derives table name from the entity type name
/// and schema from the module namespace (e.g., <c>MMCA.Modules.Catalog.Domain</c> maps to schema <c>Catalog</c>).
/// Configures key generation based on whether the entity's ID type supports auto-generation.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
[UseDataSource(DataSource.SQLServer)]
public abstract class EntityTypeConfigurationSQLServer<TEntity, TIdentifierType>
    : EntityTypeConfigurationBase<TEntity, TIdentifierType>,
      IEntityTypeConfigurationSQLServer<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<TEntity> builder)
    {
        base.Configure(builder);

        // Derive the SQL schema from the module namespace segment preceding "Domain".
        // E.g., "MMCA.Store.Sales.Domain.Orders" -> schema "Sales", table "Order".
        // Also handles legacy "MMCA.Modules.Catalog.Domain" layout. The same derivation
        // feeds the default logical database name, so schema and database can never drift.
        var schema = NamespaceConventions.GetModuleName(typeof(TEntity)) ?? "dbo";

        builder.ToTable(typeof(TEntity).Name, schema);

        builder.HasKey(p => p.Id);

        // ValueGeneratedOnAdd for numeric/GUID keys (SQL Server IDENTITY / NEWSEQUENTIALID);
        // ValueGeneratedNever for string or composite keys.
        if (typeof(TEntity).IsIdValueGenerated)
            builder.Property(p => p.Id).ValueGeneratedOnAdd();
        else
            builder.Property(p => p.Id).ValueGeneratedNever();
    }
}
