using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>
/// Normalizes every <see cref="DateTime"/> written to, and read back from, a PostgreSQL database to
/// <see cref="DateTimeKind.Utc"/>.
/// <para>
/// Npgsql maps <see cref="DateTime"/> to <c>timestamp with time zone</c> and refuses to write a
/// value whose <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>. Every timestamp
/// the framework itself produces is already UTC (the audit interceptor stamps
/// <c>TimeProvider.GetUtcNow().UtcDateTime</c>, the outbox copies the event's
/// <c>DateOccurred</c>), but a consumer's own entity property, a value deserialized from JSON, or a
/// value read from a legacy row can arrive as <see cref="DateTimeKind.Unspecified"/>, and that one
/// value would throw at save time. Converting here keeps the PostgreSQL path working for the same
/// entity code that runs unchanged on SQL Server and SQLite.
/// </para>
/// <para>
/// The alternative, the process-wide <c>Npgsql.EnableLegacyTimestampBehavior</c> switch, is
/// deliberately NOT used: it is an <c>AppContext</c> flag that changes the mapping for every Npgsql
/// connection in the host, including ones the framework does not own, and it stores timestamps
/// without a time zone, which loses the guarantee that a stored audit stamp means UTC.
/// </para>
/// </summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    /// <summary>
    /// Initializes the converter. Public on an internal type on purpose: EF instantiates a converter
    /// named by type (<c>HaveConversion&lt;T&gt;()</c>) through <c>Activator.CreateInstance</c>, which
    /// looks for a PUBLIC parameterless constructor and throws on an internal one.
    /// </summary>
    public UtcDateTimeConverter()
        : base(
            value => value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
