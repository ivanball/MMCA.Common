using System.Globalization;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Messaging;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// The consumer-side half of context propagation: reads the <see cref="MessageHeaders"/> headers
/// <c>BrokerMessageBus</c> stamped and restores them onto the per-message scope, so a
/// broker-delivered handler sees the same identity, tenant and correlation id an in-process dispatch
/// would have seen.
/// </summary>
/// <remarks>
/// Shared by every consumer the receive pipeline dispatches to, so a message reaching a retired
/// contract through <see cref="UpcastingIntegrationEventConsumer{TEvent}"/> is restored exactly as one
/// reaching the current contract through <see cref="IntegrationEventConsumer{TEvent}"/>. It lives here
/// rather than beside <see cref="AmbientOrigin"/> because it is the one step of the restore that knows
/// about a transport.
/// </remarks>
internal static class ConsumerOriginRestore
{
    /// <summary>
    /// Restores the publisher's context from <paramref name="headers"/> onto
    /// <paramref name="services"/>. A message carrying no headers (an older publisher, or an event
    /// raised with no user) leaves the scope's defaults untouched.
    /// </summary>
    /// <param name="headers">The delivered message's headers.</param>
    /// <param name="services">The per-message scope to restore onto.</param>
    /// <param name="authenticationType">The authentication type to stamp on the rebuilt identity.</param>
    /// <returns>The handle that withdraws the published origin; hold it for the whole consume.</returns>
    internal static IDisposable Apply(IReadOnlyDictionary<string, string> headers, IServiceProvider services, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // The id is transported as text and parsed back: a malformed value is a publisher bug, not a
        // reason to fail the consume, so it simply yields no identity.
        UserIdentifierType? userId =
            UserIdentifierType.TryParse(
                Get(headers, MessageHeaders.UserId),
                CultureInfo.InvariantCulture,
                out var parsedUserId)
                    ? parsedUserId
                    : null;

        return AmbientOrigin.Restore(
            services,
            userId,
            Get(headers, MessageHeaders.UserRoles),
            Get(headers, MessageHeaders.UserPermissions),
            Get(headers, MessageHeaders.TenantId),
            Get(headers, MessageHeaders.CorrelationId),
            authenticationType);
    }

    private static string? Get(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;
}
