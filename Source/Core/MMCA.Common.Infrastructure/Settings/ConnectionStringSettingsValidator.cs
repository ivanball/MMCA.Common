using Microsoft.Extensions.Options;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Startup validation for <see cref="ConnectionStringSettings"/>, registered by
/// <c>AddInfrastructure</c> with <c>ValidateOnStart</c>. It enforces one rule: the host must be able
/// to reach at least one database.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a <c>[Required]</c> data annotation on
/// <see cref="ConnectionStringSettings.SQLServerConnectionString"/>. That annotation encoded "SQL
/// Server is the only engine a host can boot on", which is no longer true: a small application can
/// run entirely on SQLite (or Cosmos), declaring its databases through the <c>DataSources</c>
/// section and leaving the top-level <c>ConnectionStrings</c> section empty. The annotation failed
/// that host at startup even though every one of its entities resolved to a configured database.
/// </para>
/// <para>
/// The rule is deliberately not weakened for the hosts that do run on SQL Server: a host with NO
/// connection string anywhere, top level or named, still fails to start. Silently booting one would
/// trade a clear startup failure for an <c>InvalidOperationException</c> on the first query, or
/// worse, a service reporting healthy while unable to serve a single request.
/// </para>
/// </remarks>
/// <param name="dataSources">
/// The named data source entries, or <see langword="null"/> when the <c>DataSources</c> section was
/// not registered (a container that binds the settings without calling <c>AddInfrastructure</c>).
/// </param>
internal sealed class ConnectionStringSettingsValidator(DataSourcesSettings? dataSources = null)
    : IValidateOptions<ConnectionStringSettings>
{
    /// <summary>
    /// The failure reported when nothing in configuration names a database. It lists both shapes,
    /// because which one is missing depends on whether the host is a single-database monolith or a
    /// database-per-module one.
    /// </summary>
    internal const string NoDatabaseConfiguredMessage =
        "No database connection is configured. Set a top-level connection string "
        + "(ConnectionStrings:SQLServerConnectionString, ConnectionStrings:SqliteConnectionString or "
        + "ConnectionStrings:CosmosConnectionString), or declare one on a named entry under the "
        + "DataSources section (for example DataSources:Tickets:SqliteConnectionString). A host with "
        + "no database at all cannot serve a request, so it fails here rather than on its first query.";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ConnectionStringSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return HasTopLevelConnection(options) || HasNamedConnection()
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(NoDatabaseConfiguredMessage);
    }

    /// <summary>Whether the top-level section names a database on any supported engine.</summary>
    private static bool HasTopLevelConnection(ConnectionStringSettings options) =>
        !string.IsNullOrWhiteSpace(options.SQLServerConnectionString)
        || !string.IsNullOrWhiteSpace(options.SqliteConnectionString)
        || !string.IsNullOrWhiteSpace(options.CosmosConnectionString);

    /// <summary>
    /// Whether any <c>DataSources</c> entry names a database. One entry is enough: an entry carrying
    /// a connection string is a physical source the resolver registers, so the host has somewhere to
    /// read and write even with the top-level section empty.
    /// </summary>
    private bool HasNamedConnection() =>
        dataSources is not null
        && dataSources.Sources.Values.Any(entry =>
            !string.IsNullOrWhiteSpace(entry.SQLServerConnectionString)
            || !string.IsNullOrWhiteSpace(entry.SqliteConnectionString)
            || !string.IsNullOrWhiteSpace(entry.CosmosConnectionString));
}
