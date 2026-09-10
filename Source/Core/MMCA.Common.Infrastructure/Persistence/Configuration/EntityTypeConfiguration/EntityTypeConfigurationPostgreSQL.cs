using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

/// <summary>
/// PostgreSQL entity configuration base — a thin shim that fixes the engine to
/// <see cref="DataSource.PostgreSQL"/>. All mapping logic (table name + module schema, key
/// generation) lives in the engine-aware <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/>,
/// which reads the <see cref="UseDataSourceAttribute"/> this shim carries. Deriving from this base is
/// equivalent to deriving from <see cref="EntityTypeConfiguration{TEntity, TIdentifierType}"/> and
/// annotating the concrete class with <c>[UseDataSource(DataSource.PostgreSQL)]</c>.
/// <para>
/// The mapping it produces is the SQL Server one (a table per entity inside the module schema, an
/// identity key), not PostgreSQL house style: moving an entity between the two engines is a single
/// base-class change with no configuration-body edits.
/// </para>
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
[UseDataSource(DataSource.PostgreSQL)]
public abstract class EntityTypeConfigurationPostgreSQL<TEntity, TIdentifierType>
    : EntityTypeConfiguration<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
