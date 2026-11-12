using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Webhooks;

/// <summary>
/// Outbound webhook options, bound from the <c>Webhooks</c> section by <c>AddWebhooks(configuration)</c>.
/// Nothing is delivered and the <c>WebhookSubscriptions</c> table is not mapped until
/// <see cref="Enabled"/> is true. Retries come from the internal-command settings
/// (<c>InternalCommands:*</c>), because every delivery is one internal-command row.
/// </summary>
public sealed class WebhookSettings
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Webhooks";

    /// <summary>Gets a value indicating whether outbound webhooks are on. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the logical data source whose database holds the <c>WebhookSubscriptions</c> table. Defaults to
    /// <c>"Default"</c>.
    /// </summary>
    public string DataSourceName { get; init; } = "Default";

    /// <summary>Gets the delivery timeout of one POST, in seconds. Defaults to 10.</summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Gets the largest event payload, in UTF-8 bytes, that is delivered; a larger event is skipped with a
    /// warning. Defaults to 65536.
    /// </summary>
    [Range(1024, 1048576)]
    public int MaxPayloadBytes { get; init; } = 65536;

    /// <summary>Gets how long, in hours, a rotated-out secret keeps signing beside the new one. Defaults to 24.</summary>
    [Range(0, 168)]
    public int PreviousSecretGraceHours { get; init; } = 24;

    /// <summary>Gets the most non-deleted subscriptions one tenant may hold. Defaults to 25.</summary>
    [Range(1, 500)]
    public int MaxSubscriptionsPerTenant { get; init; } = 25;

    /// <summary>
    /// Gets a value indicating whether http, localhost and private-address targets are accepted. Honoured
    /// only when the host environment is Development; elsewhere it is ignored with one startup warning.
    /// </summary>
    public bool AllowInsecureTargetsInDevelopment { get; init; }
}
