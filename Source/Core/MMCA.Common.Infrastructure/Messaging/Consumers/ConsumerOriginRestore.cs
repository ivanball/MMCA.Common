using System.Globalization;
using MassTransit;
using MMCA.Common.Infrastructure.Context;

// Aliased, not imported: MassTransit ships its own static MessageHeaders (its well-known transport
// header names), so the bare name is ambiguous in any file that speaks both vocabularies.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// The consumer-side half of context propagation: reads the <see cref="MessageHeaders"/> headers
/// <c>BrokerMessageBus</c> stamped and restores them onto the per-message scope, so a
/// broker-delivered handler sees the same identity, tenant and correlation id an in-process dispatch
/// would have seen.
/// </summary>
/// <remarks>
/// Shared by every consumer that binds a queue, so a message reaching a retired contract through
/// <see cref="UpcastingIntegrationEventConsumer{TEvent}"/> is restored exactly as one reaching the
/// current contract through <see cref="IntegrationEventConsumer{TEvent}"/>. It lives here rather
/// than beside <see cref="AmbientOrigin"/> because it is the one step of the restore that knows
/// about a transport.
/// </remarks>
internal static class ConsumerOriginRestore
{
    /// <summary>
    /// Restores the publisher's context from <paramref name="headers"/> onto
    /// <paramref name="services"/>. A message carrying no headers (an older publisher, or an event
    /// raised with no user) leaves the scope's defaults untouched.
    /// </summary>
    /// <param name="headers">The consume context's headers.</param>
    /// <param name="services">The per-message scope to restore onto.</param>
    /// <param name="authenticationType">The authentication type to stamp on the rebuilt identity.</param>
    internal static void Apply(Headers headers, IServiceProvider services, string authenticationType)
    {
        // The id is transported as text and parsed back rather than read as a typed header: a broker
        // is free to widen an integer header (Azure Service Bus hands back a long), and a parse of
        // the canonical form is the one reading that behaves the same on every transport. A
        // malformed value is a publisher bug, not a reason to fail the consume, so it simply yields
        // no identity.
        UserIdentifierType? userId =
            UserIdentifierType.TryParse(
                headers.Get<string>(MessageHeaders.UserId, null),
                CultureInfo.InvariantCulture,
                out var parsedUserId)
                ? parsedUserId
                : null;

        AmbientOrigin.Restore(
            services,
            userId,
            headers.Get<string>(MessageHeaders.UserRoles, null),
            headers.Get<string>(MessageHeaders.TenantId, null),
            headers.Get<string>(MessageHeaders.CorrelationId, null),
            authenticationType);
    }
}
