using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// SQLite entity configuration base — a thin shim that fixes the engine to
/// <see cref="DataSource.Sqlite"/>. All mapping logic (table name, auto-increment key) lives in the
/// engine-aware <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/>, which reads the
/// <see cref="UseDataSourceAttribute"/> this shim carries. Deriving from this base is equivalent to
/// deriving from <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/> and annotating the
/// concrete class with <c>[UseDataSource(DataSource.Sqlite)]</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
[UseDataSource(DataSource.Sqlite)]
public abstract class EntityTypeConfigurationSqlite<TEntity, TIdentifierType>
    : EntityTypeConfiguration<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
