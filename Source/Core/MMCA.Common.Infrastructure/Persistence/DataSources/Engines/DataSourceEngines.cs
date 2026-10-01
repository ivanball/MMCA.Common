using System.Collections.Frozen;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// The one registry of database engines, keyed by the shipped <see cref="DataSource"/> value.
/// <para>
/// Static rather than a DI service because several consumers run where no container is reachable
/// (model-building conventions, <c>EntityTypeConfiguration.ApplyEngineConventions</c>,
/// <c>SoftDeleteFilterSql</c>, <c>IndexBuilderExtensions</c>), and because engines are stateless
/// facts about a closed enum that only Common can extend, so a DI extension point would buy no extensibility.
/// </para>
/// <para>
/// Adding a fifth engine: one new <see cref="IDataSourceEngine"/> class plus one line in
/// <see cref="Registered"/>.
/// </para>
/// </summary>
internal static class DataSourceEngines
{
    /// <summary>Every engine, in registration order.</summary>
    private static readonly IDataSourceEngine[] Registered =
    [
        new CosmosDataSourceEngine(),
        new PostgreSQLDataSourceEngine(),
        new SqliteDataSourceEngine(),
        new SQLServerDataSourceEngine(),
    ];

    private static readonly FrozenDictionary<DataSource, IDataSourceEngine> ByEngine =
        Registered.ToFrozenDictionary(engine => engine.Engine);

    /// <summary>Gets every registered engine, in registration order.</summary>
    internal static IReadOnlyList<IDataSourceEngine> All => Registered;

    /// <summary>Gets the engine registered for <paramref name="engine"/>.</summary>
    /// <param name="engine">The engine value.</param>
    /// <returns>The registered engine.</returns>
    /// <exception cref="InvalidOperationException">No engine is registered for the value.</exception>
    internal static IDataSourceEngine For(DataSource engine) =>
        ByEngine.TryGetValue(engine, out var registered)
            ? registered
            : throw new InvalidOperationException($"DataSource \"{engine}\" not implemented.");
}
