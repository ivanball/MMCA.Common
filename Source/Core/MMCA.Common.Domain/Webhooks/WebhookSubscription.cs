using System.Diagnostics.CodeAnalysis;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Webhooks;

/// <summary>
/// One outbound webhook subscription: a target URL that receives a signed POST for every published
/// integration event whose webhook type it lists (or every type, with <see cref="AllEventTypes"/>).
/// <para>
/// <b>Opt-in table.</b> Mapped only where a host calls <c>AddWebhooks</c> with <c>Webhooks:Enabled</c>,
/// in the data source <c>Webhooks:DataSourceName</c> names; a host that never opts in builds an
/// unchanged model.
/// </para>
/// <para>
/// <b>Secrets are stored protected.</b> <see cref="ProtectedSecret"/> holds the signing secret as the
/// host's data-protection layer protected it; the plaintext is returned once, at create or rotate.
/// After a rotation the previous secret keeps signing beside the new one until
/// <see cref="PreviousSecretExpiresOn"/>, so a receiver can switch without dropping deliveries.
/// </para>
/// <para>
/// <b>Tenant.</b> <see cref="TenantId"/> is a plain column stamped at create from the caller's tenant,
/// deliberately not <c>ITenantEntity</c>: the store filters it explicitly, null matching only null.
/// </para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "The target URL is untrusted operator input, validated here as text (scheme, user info, fragment, length) before anything parses it, and stored verbatim; a Uri parameter would already have normalized it.")]
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "The target URL is stored verbatim in an nvarchar column and returned verbatim to the operator; System.Uri would re-normalize it.")]
[SuppressMessage(
    "Naming",
    "CA1721:Property names should not match get methods",
    Justification = "EventTypes is the stored comma-joined column EF maps; GetEventTypes() is its parsed list. Both are the declared shape (a property for the list would map as a second column).")]
public sealed class WebhookSubscription : AuditableBaseEntity<Guid>
{
    /// <summary>The longest target URL accepted, and the column width.</summary>
    public const int UrlMaxLength = 2048;

    /// <summary>The longest description accepted, and the column width.</summary>
    public const int DescriptionMaxLength = 200;

    /// <summary>The width of the comma-joined event-type column.</summary>
    public const int EventTypesMaxLength = 2048;

    /// <summary>The most event types one subscription may list.</summary>
    public const int MaxEventTypes = 20;

    /// <summary>The width of the protected-secret columns.</summary>
    public const int ProtectedSecretMaxLength = 1024;

    /// <summary>The width of the tenant column.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>The width of the last-failure column; longer reasons are truncated.</summary>
    public const int LastFailureMaxLength = 500;

    /// <summary>The event-type entry that subscribes to every registered webhook event.</summary>
    public const string AllEventTypes = "*";

    private const char EventTypeSeparator = ',';

    private WebhookSubscription()
    {
    }

    /// <summary>Gets the absolute http(s) URL deliveries are posted to.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>Gets the operator's label for the subscription, or <see langword="null"/>.</summary>
    public string? Description { get; private set; }

    /// <summary>Gets the subscribed event types, ordinal-sorted, distinct and comma-joined.</summary>
    public string EventTypes { get; private set; } = string.Empty;

    /// <summary>Gets the tenant the subscription belongs to, or <see langword="null"/> in an untenanted host.</summary>
    public string? TenantId { get; private set; }

    /// <summary>Gets the current signing secret, as protected by the host.</summary>
    public string ProtectedSecret { get; private set; } = string.Empty;

    /// <summary>Gets the rotated-out signing secret, as protected by the host, or <see langword="null"/>.</summary>
    public string? PreviousProtectedSecret { get; private set; }

    /// <summary>Gets the UTC instant the previous secret stops signing, or <see langword="null"/>.</summary>
    public DateTime? PreviousSecretExpiresOn { get; private set; }

    /// <summary>Gets the UTC instant the subscription was disabled, or <see langword="null"/> while it is enabled.</summary>
    public DateTime? DisabledOn { get; private set; }

    /// <summary>Gets the UTC instant of the last successful delivery, or <see langword="null"/>.</summary>
    public DateTime? LastDeliveredOn { get; private set; }

    /// <summary>Gets the UTC instant of the last failed delivery, or <see langword="null"/>.</summary>
    public DateTime? LastFailedOn { get; private set; }

    /// <summary>Gets the reason of the last failed delivery, or <see langword="null"/>.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Creates an enabled subscription.</summary>
    /// <param name="url">The absolute http(s) target URL, without user info or fragment.</param>
    /// <param name="description">An optional label of up to <see cref="DescriptionMaxLength"/> characters.</param>
    /// <param name="eventTypes">1 to <see cref="MaxEventTypes"/> non-blank event types, or <see cref="AllEventTypes"/>.</param>
    /// <param name="tenantId">The caller's tenant, or <see langword="null"/>.</param>
    /// <param name="protectedSecret">The signing secret as protected by the host.</param>
    /// <returns>The subscription, or a validation failure.</returns>
    public static Result<WebhookSubscription> Create(
        string url,
        string? description,
        IReadOnlyCollection<string> eventTypes,
        string? tenantId,
        string protectedSecret)
    {
        var errors = ValidateDefinition(url, description, eventTypes, nameof(Create), out var normalizedDescription, out var joinedEventTypes);
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            errors.Add(Error.Validation(
                "Webhooks.SecretRequired", "A webhook subscription needs a signing secret.", nameof(Create), nameof(protectedSecret)));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<WebhookSubscription>(errors);
        }

        return Result.Success(new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            Description = normalizedDescription,
            EventTypes = joinedEventTypes,
            TenantId = tenantId,
            ProtectedSecret = protectedSecret,
        });
    }

    /// <summary>Replaces the target URL, description and event types; the secrets and outcome columns are kept.</summary>
    /// <param name="url">The absolute http(s) target URL, without user info or fragment.</param>
    /// <param name="description">An optional label of up to <see cref="DescriptionMaxLength"/> characters.</param>
    /// <param name="eventTypes">1 to <see cref="MaxEventTypes"/> non-blank event types, or <see cref="AllEventTypes"/>.</param>
    /// <returns>A success result, or a validation failure that leaves the subscription unchanged.</returns>
    public Result Update(string url, string? description, IReadOnlyCollection<string> eventTypes)
    {
        var errors = ValidateDefinition(url, description, eventTypes, nameof(Update), out var normalizedDescription, out var joinedEventTypes);
        if (errors.Count > 0)
        {
            return Result.Failure(errors);
        }

        Url = url;
        Description = normalizedDescription;
        EventTypes = joinedEventTypes;
        return Result.Success();
    }

    /// <summary>Whether deliveries are currently made to this subscription.</summary>
    /// <returns><see langword="true"/> while <see cref="DisabledOn"/> is <see langword="null"/>.</returns>
    public bool IsEnabled() => DisabledOn is null;

    /// <summary>Gets the subscribed event types as a list.</summary>
    /// <returns>The ordinal-sorted, distinct event types.</returns>
    public IReadOnlyList<string> GetEventTypes() =>
        EventTypes.Length == 0 ? [] : EventTypes.Split(EventTypeSeparator);

    /// <summary>Whether the subscription receives <paramref name="eventType"/>.</summary>
    /// <param name="eventType">A registered webhook event type.</param>
    /// <returns><see langword="true"/> when the type is listed, or the subscription lists <see cref="AllEventTypes"/>.</returns>
    public bool Subscribes(string eventType)
    {
        var listed = GetEventTypes();
        return listed.Contains(AllEventTypes, StringComparer.Ordinal) || listed.Contains(eventType, StringComparer.Ordinal);
    }

    /// <summary>
    /// Makes <paramref name="newProtectedSecret"/> the current secret and keeps the old one signing until
    /// <paramref name="previousSecretExpiresOn"/>.
    /// </summary>
    /// <param name="newProtectedSecret">The new signing secret, as protected by the host.</param>
    /// <param name="previousSecretExpiresOn">The UTC instant the old secret stops signing.</param>
    public void RotateSecret(string newProtectedSecret, DateTime previousSecretExpiresOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newProtectedSecret);

        PreviousProtectedSecret = ProtectedSecret;
        PreviousSecretExpiresOn = previousSecretExpiresOn;
        ProtectedSecret = newProtectedSecret;
    }

    /// <summary>Gets the protected secrets a delivery at <paramref name="utcNow"/> is signed with.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <returns>The current secret first, then the previous one while <paramref name="utcNow"/> is before its expiry.</returns>
    public IReadOnlyList<string> GetSigningSecrets(DateTime utcNow) =>
        PreviousProtectedSecret is { } previous && PreviousSecretExpiresOn is { } expiresOn && utcNow < expiresOn
            ? [ProtectedSecret, previous]
            : [ProtectedSecret];

    /// <summary>Stops deliveries. Disabling a disabled subscription keeps the first instant.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public void Disable(DateTime utcNow) => DisabledOn ??= utcNow;

    /// <summary>Resumes deliveries.</summary>
    public void Enable() => DisabledOn = null;

    /// <summary>Records a successful delivery.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public void RecordDelivered(DateTime utcNow) => LastDeliveredOn = utcNow;

    /// <summary>Records a failed delivery; the reason is truncated to <see cref="LastFailureMaxLength"/> characters.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <param name="reason">Why the delivery failed (<c>"HTTP 500"</c>, or the exception type and message).</param>
    public void RecordFailed(DateTime utcNow, string reason)
    {
        var text = reason ?? string.Empty;
        LastFailedOn = utcNow;
        LastFailure = text.Length > LastFailureMaxLength ? text[..LastFailureMaxLength] : text;
    }

    private static List<Error> ValidateDefinition(
        string url,
        string? description,
        IReadOnlyCollection<string> eventTypes,
        string source,
        out string? normalizedDescription,
        out string joinedEventTypes)
    {
        List<Error> errors = [];

        if (!IsValidUrl(url))
        {
            errors.Add(Error.Validation(
                "Webhooks.UrlInvalid",
                "A webhook URL must be an absolute https or http URL of at most 2048 characters, without user info or fragment.",
                source,
                nameof(url)));
        }

        normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalizedDescription is { Length: > DescriptionMaxLength })
        {
            errors.Add(Error.Validation(
                "Webhooks.DescriptionTooLong", "A webhook description is at most 200 characters.", source, nameof(description)));
        }

        joinedEventTypes = string.Empty;
        if (eventTypes is null || eventTypes.Count == 0 || eventTypes.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add(Error.Validation(
                "Webhooks.EventTypesRequired", "A webhook subscription needs at least one non-blank event type.", source, nameof(eventTypes)));
            return errors;
        }

        var normalized = eventTypes
            .Select(eventType => eventType.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var joined = string.Join(EventTypeSeparator, normalized);

        if (normalized.Count > MaxEventTypes || joined.Length > EventTypesMaxLength)
        {
            errors.Add(Error.Validation(
                "Webhooks.TooManyEventTypes", "A webhook subscription lists at most 20 event types.", source, nameof(eventTypes)));
            return errors;
        }

        joinedEventTypes = joined;
        return errors;
    }

    private static bool IsValidUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.Length <= UrlMaxLength
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
        && !url.Contains('#', StringComparison.Ordinal);
}
