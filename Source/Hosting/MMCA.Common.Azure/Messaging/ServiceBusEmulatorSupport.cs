using System.Globalization;
using Azure.Messaging.ServiceBus.Administration;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The pieces the Azure Service Bus transport needs to run against the OFFICIAL local emulator
/// instead of a real namespace, kept in one place so the production path reads only one flag
/// (<c>AzureServiceBusEndpoint.IsEmulator</c>), set from the connection string.
/// <para>
/// The emulator is entered only when the resolved connection string carries
/// <c>UseDevelopmentEmulator=true</c> (<see cref="IsEmulatorConnectionString"/>). A real Azure
/// Service Bus connection string never carries that token, so no production deployment can reach any
/// of this by accident: that is the whole reason detection keys off the connection string rather than
/// an environment name or a separate flag anyone could set in the wrong place.
/// </para>
/// <para>
/// Two things make the emulator different from the real service. It serves the data plane (AMQP) and
/// the management plane (HTTP, where a <see cref="ServiceBusAdministrationClient"/> creates topics,
/// subscriptions and queues) on two ports, so the administration client needs its own connection
/// string (<see cref="BuildAdminConnectionString"/>). And it enforces a one-hour ceiling on entity
/// time-to-live and auto-delete (<see cref="EmulatorEntityQuota"/>), so every entity the transport
/// provisions there must stay beneath it.
/// </para>
/// </summary>
internal static class ServiceBusEmulatorSupport
{
    /// <summary>
    /// The token an emulator connection string carries and a real namespace never does.
    /// </summary>
    internal const string EmulatorMarker = "UseDevelopmentEmulator=true";

    /// <summary>
    /// The ceiling the emulator enforces on entity time-to-live and auto-delete-on-idle.
    /// </summary>
    internal static readonly TimeSpan EmulatorEntityQuota = TimeSpan.FromHours(1);

    /// <summary>
    /// Reports whether <paramref name="connectionString"/> targets the local emulator rather than a
    /// real Azure Service Bus namespace.
    /// </summary>
    /// <param name="connectionString">The resolved broker connection string.</param>
    /// <returns><see langword="true"/> when the emulator marker is present.</returns>
    internal static bool IsEmulatorConnectionString(string? connectionString) =>
        connectionString is not null
        && connectionString.Contains(EmulatorMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the management-plane connection string from the AMQP one by swapping in the admin
    /// endpoint's host and port. Rebuilding it from the data-plane string (rather than composing a
    /// fresh one) is what keeps the shared-access key name and value identical across both clients:
    /// the two planes authenticate against the same emulator namespace, so a hand-written second
    /// string is one silent typo away from an admin client that cannot provision anything.
    /// </summary>
    /// <param name="connectionString">The emulator AMQP connection string.</param>
    /// <param name="adminEndpoint">The management-plane base address, an absolute http/https URL.</param>
    /// <returns>The connection string for the management-plane client.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="adminEndpoint"/> is missing or malformed.</exception>
    internal static string BuildAdminConnectionString(string connectionString, string? adminEndpoint)
    {
        // An absolute URI is not enough: "localhost:5300" parses as one, with "localhost" read as the
        // scheme and no host at all, which would silently produce an admin string pointing nowhere.
        // Requiring http or https is what makes a bare host:port fail here instead.
        if (string.IsNullOrWhiteSpace(adminEndpoint)
            || !Uri.TryCreate(adminEndpoint, UriKind.Absolute, out Uri? adminUri)
            || !string.Equals(adminUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(adminUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MessageBus:EmulatorAdminEndpoint is required when MessageBus:ConnectionString targets the Azure Service Bus emulator (it carries UseDevelopmentEmulator=true). The transport provisions its topics, subscriptions and queues through a management-plane client in addition to the AMQP one, and the emulator serves that plane on a different port. Set it to the emulator's management-plane base address, for example 'http://localhost:5300'; an Aspire AppHost gets this for free from WithBroker(serviceBusEmulator).");
        }

        string endpointSegment = string.Create(
            CultureInfo.InvariantCulture,
            $"Endpoint=sb://{adminUri.Host}:{adminUri.Port}");

        IEnumerable<string> segments = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => segment.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)
                ? endpointSegment
                : segment);

        return string.Join(';', segments) + ';';
    }
}
