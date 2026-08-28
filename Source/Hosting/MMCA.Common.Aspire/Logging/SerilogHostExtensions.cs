using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MMCA.Common.Aspire.Logging;

/// <summary>
/// The Serilog bootstrap every MMCA service host repeats verbatim before
/// <c>AddServiceDefaults()</c>: console always, a rolling file sink outside Production, the
/// framework's minimum-level overrides, and registration as ONE logging provider.
/// <para>
/// The provider registration is the load-bearing part. <c>UseSerilog()</c> replaces the whole
/// <c>ILoggerFactory</c> and so silently bypasses every other provider, including the OpenTelemetry
/// to Azure Monitor one wired by <c>AddServiceDefaults()</c>: a host that calls it publishes no
/// application log line to App Insights at all. <c>builder.Logging.AddSerilog(...)</c> adds Serilog
/// alongside the others instead, which is why this helper does that and never the former.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with an extension(T) block in a static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class SerilogHostExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Configures the global <see cref="Log.Logger"/> from the framework defaults and registers
        /// it as one logging provider on <paramref name="builder"/>. Call it before
        /// <c>AddServiceDefaults()</c>, as the hosts do, so the OpenTelemetry provider registered
        /// there joins the same factory.
        /// </summary>
        /// <param name="logFilePath">
        /// Path of the rolling log file, relative to the content root (for example
        /// <c>"logs/MyService.txt"</c>). Ignored in Production, where the file sink is not added.
        /// </param>
        /// <param name="configure">
        /// Optional hook applied to the configuration after the defaults and before the logger is
        /// created, for a host that needs an extra sink or enricher of its own.
        /// </param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="logFilePath"/> is null or whitespace.</exception>
        public WebApplicationBuilder AddCommonSerilog(
            string logFilePath,
            Action<LoggerConfiguration>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            Log.Logger = CreateLoggerConfiguration(builder.Environment, logFilePath, configure).CreateLogger();
            builder.Logging.AddSerilog(Log.Logger, dispose: true);

            return builder;
        }
    }

    /// <summary>
    /// Creates a logger factory writing to the global <see cref="Log.Logger"/>, for the startup-time
    /// diagnostics a host needs before the DI container exists (module discovery above all).
    /// Dispose it once startup wiring is done; it does not own <see cref="Log.Logger"/>.
    /// </summary>
    /// <returns>A logger factory backed by the current global Serilog logger.</returns>
    public static ILoggerFactory CreateBootstrapLoggerFactory() =>
        LoggerFactory.Create(logging => logging.AddSerilog());

    /// <summary>
    /// The minimum level the framework defaults to: <see cref="LogEventLevel.Debug"/> in Development,
    /// <see cref="LogEventLevel.Information"/> everywhere else.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <returns>The resolved minimum level.</returns>
    internal static LogEventLevel ResolveMinimumLevel(IHostEnvironment environment) =>
        environment.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Information;

    /// <summary>
    /// Whether the rolling file sink applies. Production containers write to ephemeral disk nothing
    /// reads (stdout and OTel already carry production logs), so the sink is added everywhere except
    /// Production: local development and the CI E2E stack, where the file is what a failure is
    /// diagnosed from.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true"/> when the file sink should be added.</returns>
    internal static bool ShouldWriteFileSink(IHostEnvironment environment) =>
        !environment.IsProduction();

    /// <summary>
    /// Builds the framework's logger configuration without creating or publishing a logger, so the
    /// shape can be asserted in isolation.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <param name="logFilePath">Path of the rolling log file, relative to the content root.</param>
    /// <param name="configure">Optional post-defaults hook.</param>
    /// <returns>The configured, uncreated logger configuration.</returns>
    internal static LoggerConfiguration CreateLoggerConfiguration(
        IHostEnvironment environment,
        string logFilePath,
        Action<LoggerConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(ResolveMinimumLevel(environment))
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);

        if (ShouldWriteFileSink(environment))
        {
            loggerConfiguration = loggerConfiguration.WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                formatProvider: CultureInfo.InvariantCulture);
        }

        configure?.Invoke(loggerConfiguration);

        return loggerConfiguration;
    }
}
