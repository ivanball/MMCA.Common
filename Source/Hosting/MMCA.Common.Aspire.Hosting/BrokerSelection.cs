using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The AppHost broker selector: RabbitMQ by default, the official Azure Service Bus emulator when the
/// app's environment variable reads <see cref="ServiceBusSelection"/> (rubric section 33, environment
/// parity). The emulator costs a second container plus a warm-up, which is not a price the everyday
/// inner loop should pay, so it is opt-in per run.
/// <para>
/// The two <c>WithBroker</c> overloads take different resource types, so the choice cannot be expressed
/// as one variable handed to one call. <see cref="AddSelectedBroker"/> makes the choice once and returns
/// it as an attach delegate, and <see cref="WithSelectedBroker"/> applies it, which keeps every service's
/// wiring chain reading the same single line and stops a new service from quietly being wired to the
/// wrong broker. The variable's name and everything else about the stack stay in the app's AppHost.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class BrokerSelection
{
    /// <summary>The environment-variable value (case-insensitive) that selects the Service Bus emulator.</summary>
    public const string ServiceBusSelection = "servicebus";

    /// <summary>
    /// Whether the named environment variable selects the Azure Service Bus emulator. Unset, or any
    /// other value, selects RabbitMQ.
    /// </summary>
    /// <param name="environmentVariableName">The app's selector variable, for example <c>ADC_BROKER</c>.</param>
    /// <returns><see langword="true"/> when the variable reads <see cref="ServiceBusSelection"/>.</returns>
    public static bool IsServiceBusSelected(string environmentVariableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariableName);

        return string.Equals(
            Environment.GetEnvironmentVariable(environmentVariableName),
            ServiceBusSelection,
            StringComparison.OrdinalIgnoreCase);
    }

    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Provisions the broker the named environment variable selects and returns the attach delegate
        /// to hand to <see cref="WithSelectedBroker"/> on every service. Service Bus emulator:
        /// <c>AddServiceBusEmulatorBroker(sqlServer)</c>. Otherwise RabbitMQ:
        /// <c>AddMessageBroker()</c> with a persistent container lifetime, so the broker survives an
        /// AppHost restart.
        /// </summary>
        /// <param name="environmentVariableName">The app's selector variable, for example <c>ADC_BROKER</c>.</param>
        /// <param name="sqlServer">The SQL Server resource the emulator keeps its state in.</param>
        /// <returns>The attach delegate that wires one service to the selected broker.</returns>
        public Func<IResourceBuilder<ProjectResource>, IResourceBuilder<ProjectResource>> AddSelectedBroker(
            string environmentVariableName,
            IResourceBuilder<SqlServerServerResource> sqlServer)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(sqlServer);

            if (IsServiceBusSelected(environmentVariableName))
            {
                var serviceBus = builder.AddServiceBusEmulatorBroker(sqlServer);
                return service => service.WithBroker(serviceBus);
            }

            var rabbit = builder.AddMessageBroker()
                .WithLifetime(ContainerLifetime.Persistent);
            return service => service.WithBroker(rabbit);
        }
    }

    extension(IResourceBuilder<ProjectResource> service)
    {
        /// <summary>Attaches the selected broker to this service resource.</summary>
        /// <param name="attach">The delegate <see cref="AddSelectedBroker"/> returned.</param>
        /// <returns>The service resource builder, for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithSelectedBroker(
            Func<IResourceBuilder<ProjectResource>, IResourceBuilder<ProjectResource>> attach)
        {
            ArgumentNullException.ThrowIfNull(attach);
            return attach(service);
        }
    }
}
