using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// SQL Server entity configuration base. Derives table name from the entity type name
/// and schema from the module namespace (e.g., <c>ADC.Modules.Catalog.Domain</c> maps to schema <c>Catalog</c>).
/// Configures key generation based on whether the entity's ID type supports auto-generation.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="dataSourceService">Used to register which data source backs this entity.</param>
[UseDataSource(DataSource.SQLServer)]
public abstract class EntityTypeConfigurationSQLServer<TEntity, TIdentifierType>(IDataSourceService dataSourceService)
    : EntityTypeConfigurationBase<TEntity, TIdentifierType>(dataSourceService),
      IEntityTypeConfigurationSQLServer<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override void Configure(EntityTypeBuilder<TEntity> builder)
    {
        base.Configure(builder);

        // Derive the SQL schema from the module namespace segment following "Modules".
        // E.g., "ADC.Modules.Sales.Domain.Order" -> schema "Sales", table "Order".
        var segments = typeof(TEntity).Namespace?.Split('.') ?? [];
        var modulesIndex = Array.FindIndex(segments,
            s => s.Equals("Modules", StringComparison.OrdinalIgnoreCase));
        var schema = modulesIndex >= 0 && modulesIndex + 1 < segments.Length
            ? segments[modulesIndex + 1]
            : "dbo";

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
