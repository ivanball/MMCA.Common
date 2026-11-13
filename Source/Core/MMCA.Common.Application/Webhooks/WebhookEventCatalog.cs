using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MMCA.Common.Application.Webhooks;

/// <summary>
/// Every webhook event type the host publishes, built from the <see cref="WebhookEventRegistration"/>
/// entries <c>AddWebhookEvent</c> adds. Empty when nothing is registered.
/// </summary>
public sealed partial class WebhookEventCatalog
{
    /// <summary>The longest webhook event type name accepted.</summary>
    internal const int MaxEventTypeLength = 100;

    private readonly Dictionary<string, Type> _byName;
    private readonly Dictionary<Type, string> _byType;

    /// <summary>Initializes a new instance of the <see cref="WebhookEventCatalog"/> class.</summary>
    /// <param name="registrations">The registrations.</param>
    public WebhookEventCatalog(IEnumerable<WebhookEventRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _byName = [with(StringComparer.Ordinal)];
        _byType = [];
        foreach (var registration in registrations)
        {
            _byName.TryAdd(registration.EventType, registration.EventClrType);
            _byType.TryAdd(registration.EventClrType, registration.EventType);
        }

        EventTypes = [.. _byName.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>Gets the registered webhook event type names, ordinal-sorted.</summary>
    public IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>Whether <paramref name="eventType"/> is registered.</summary>
    /// <param name="eventType">A webhook event type name.</param>
    /// <returns><see langword="true"/> when it is registered.</returns>
    public bool Contains(string eventType) => eventType is not null && _byName.ContainsKey(eventType);

    /// <summary>Gets the webhook event type name an integration event type is published under.</summary>
    /// <param name="eventClrType">The integration event contract type.</param>
    /// <param name="eventType">The name, when registered.</param>
    /// <returns><see langword="true"/> when the type is registered.</returns>
    public bool TryGetEventType(Type eventClrType, [NotNullWhen(true)] out string? eventType)
    {
        ArgumentNullException.ThrowIfNull(eventClrType);
        return _byType.TryGetValue(eventClrType, out eventType);
    }

    /// <summary>Whether <paramref name="eventType"/> is a valid webhook event type name.</summary>
    /// <param name="eventType">The candidate name.</param>
    /// <returns><see langword="true"/> for a lower-case dotted, dashed or underscored name of at most 100 characters.</returns>
    internal static bool IsValidEventTypeName(string? eventType) =>
        eventType is { Length: > 0 and <= MaxEventTypeLength } && EventTypeNamePattern.IsMatch(eventType);

    [GeneratedRegex("^[a-z][a-z0-9]*([._-][a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventTypeNamePattern { get; }
}
