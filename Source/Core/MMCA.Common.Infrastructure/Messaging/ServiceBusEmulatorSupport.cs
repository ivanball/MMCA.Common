using System.Globalization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using MassTransit;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// Everything the Azure Service Bus transport needs to run against the OFFICIAL local emulator
/// instead of a real namespace, kept in one place so the production path in
/// <c>ConfigureBrokerTransport</c> stays a single unconditional <c>cfg.Host(connectionString)</c>.
/// <para>
/// The emulator is entered only when the resolved connection string carries
/// <c>UseDevelopmentEmulator=true</c> (<see cref="IsEmulatorConnectionString"/>). A real Azure
/// Service Bus connection string never carries that token, so no production deployment can reach any
/// of this by accident: that is the whole reason detection keys off the connection string rather than
/// an environment name or a separate flag anyone could set in the wrong place.
/// </para>
/// <para>
/// Two things make the emulator different from the real service, and both are MassTransit v8
/// constraints rather than choices. First, v8 has no vendor emulator mode (that shipped in v9, which
/// the workspace excludes because it needs a commercial license), so the ONLY way onto the emulator
/// is the custom-clients <c>Host</c> overload, handing MassTransit a data-plane
/// <see cref="ServiceBusClient"/> and a management-plane <see cref="ServiceBusAdministrationClient"/>
/// built by the caller. The emulator serves those two planes on two ports (AMQP and HTTP), which is
/// why the admin client needs its own connection string and its own configured endpoint. Second, the
/// emulator enforces a one-hour ceiling on entity time-to-live and auto-delete, and MassTransit v8's
/// defaults sit far above it (366 days TTL, 427 days auto-delete), so every entity it tries to
/// provision is rejected until the defaults are lowered.
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
    /// The host address the custom-clients <c>Host</c> overload is anchored to. It names the bus, not
    /// a network location: both clients are already bound to the emulator's actual ports.
    /// </summary>
    private static readonly Uri EmulatorHostAddress = new("sb://localhost/");

    /// <summary>
    /// Guards <see cref="ApplyEmulatorEntityQuotas"/>. The defaults it writes are process-global
    /// MassTransit statics, so they are applied once per process and only on the emulator branch.
    /// </summary>
    private static int _entityQuotasApplied;

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
    /// Configures the bus host against the emulator: lowers the process-global entity quotas, then
    /// binds MassTransit to a data-plane and a management-plane client built from
    /// <paramref name="connectionString"/> and <paramref name="adminEndpoint"/>.
    /// </summary>
    /// <param name="cfg">The Azure Service Bus bus factory configurator.</param>
    /// <param name="connectionString">The emulator AMQP connection string.</param>
    /// <param name="adminEndpoint">
    /// The emulator management-plane base address (<c>MessageBus:EmulatorAdminEndpoint</c>), for
    /// example <c>http://localhost:32771</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="adminEndpoint"/> is missing or is not an absolute URL. Without it there is no
    /// management client, and MassTransit cannot provision the topology it needs to run at all, so
    /// this fails at registration rather than at the first publish.
    /// </exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "MassTransit takes ownership of the ServiceBusClient handed to the custom-clients Host overload and keeps it for the life of the bus, which is the life of the process. Disposing it here would close the connection before the first publish.")]
    internal static void ConfigureEmulatorHost(
        IServiceBusBusFactoryConfigurator cfg,
        string connectionString,
        string? adminEndpoint)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        ApplyEmulatorEntityQuotas();

        string adminConnectionString = BuildAdminConnectionString(connectionString, adminEndpoint);

        cfg.Host(
            EmulatorHostAddress,
            new ServiceBusClient(connectionString),
            new ServiceBusAdministrationClient(adminConnectionString));
    }

    /// <summary>
    /// Lowers MassTransit's process-global entity defaults beneath the emulator's one-hour ceiling,
    /// once per process. These are static properties on the transport, so this is deliberately NOT
    /// reached on the real-namespace path: a production bus keeps MassTransit's own defaults.
    /// </summary>
    internal static void ApplyEmulatorEntityQuotas()
    {
        if (Interlocked.Exchange(ref _entityQuotasApplied, 1) != 0)
        {
            return;
        }

        global::MassTransit.AzureServiceBusTransport.Defaults.DefaultMessageTimeToLive = EmulatorEntityQuota;
        global::MassTransit.AzureServiceBusTransport.Defaults.BasicMessageTimeToLive = EmulatorEntityQuota;
        global::MassTransit.AzureServiceBusTransport.Defaults.AutoDeleteOnIdle = EmulatorEntityQuota;
    }

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
                "MessageBus:EmulatorAdminEndpoint is required when MessageBus:ConnectionString targets the Azure Service Bus emulator (it carries UseDevelopmentEmulator=true). MassTransit v8 reaches the emulator only through the custom-clients Host overload, which needs a management-plane client in addition to the AMQP one, and the emulator serves that plane on a different port. Set it to the emulator's management-plane base address, for example 'http://localhost:5300'; an Aspire AppHost gets this for free from WithBroker(serviceBusEmulator).");
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
