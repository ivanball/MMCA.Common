using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Configuration for the idempotency filter, bound from the <c>Idempotency</c> section.
/// All properties have sensible defaults so the section is optional in <c>appsettings.json</c>.
/// </summary>
public sealed class IdempotencySettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Idempotency";

    /// <summary>Gets the duration in hours that cached idempotent responses are retained.</summary>
    [Range(1, 168)]
    public int CacheExpirationHours { get; init; } = 24;
}
