namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Identifies a physical data source: a database <see cref="Engine"/> plus a <see cref="Name"/>
/// distinguishing multiple databases on the same engine ("database per microservice").
/// <para>
/// The <see cref="Name"/> is the <b>physical</b> source name produced by the Infrastructure-layer
/// resolver after collapsing logical names that share a connection string. Application code only
/// ever compares physical keys — two entities support EF Core <c>.Include()</c> between them only
/// when their physical keys are equal (and the engine is relational).
/// </para>
/// </summary>
/// <param name="Engine">The database engine backing this source.</param>
/// <param name="Name">The physical source name; <see cref="DefaultName"/> for the top-level connection strings.</param>
public readonly record struct DataSourceKey(DataSource Engine, string Name)
{
    /// <summary>The reserved name of the default physical source (top-level <c>ConnectionStrings</c> section).</summary>
    public const string DefaultName = "Default";

    /// <summary>Creates the default physical key for the given engine.</summary>
    /// <param name="engine">The database engine.</param>
    /// <returns>The default <see cref="DataSourceKey"/> for the engine.</returns>
    public static DataSourceKey Default(DataSource engine) => new(engine, DefaultName);

    /// <inheritdoc />
    public override string ToString() => $"{Engine}/{Name}";
}
