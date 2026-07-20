using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// SQL Server entity configuration base — a thin shim that fixes the engine to
/// <see cref="DataSource.SQLServer"/>. All mapping logic (table name + module schema, key
/// generation) lives in the engine-aware <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/>,
/// which reads the <see cref="UseDataSourceAttribute"/> this shim carries. Deriving from this base is
/// equivalent to deriving from <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/> and
/// annotating the concrete class with <c>[UseDataSource(DataSource.SQLServer)]</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
[UseDataSource(DataSource.SQLServer)]
public abstract class EntityTypeConfigurationSQLServer<TEntity, TIdentifierType>
    : EntityTypeConfiguration<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
