using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// Runs when the MAUI app is built: installs the two process-wide last-chance exception handlers
/// (<see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/>) and reports what they catch to the app's
/// logger plus an optional crash-reporter callback. Registered by
/// <c>UseMmcaMauiErrorHandling</c>; a head should not construct it directly.
/// <para>
/// Why an initializer rather than the builder extension itself: the handlers need an
/// <see cref="ILogger"/>, and the container that can supply one only exists once the app is
/// built. <see cref="IMauiInitializeService.Initialize"/> is the first point where both are
/// available, which is the same reason <see cref="DeviceCapabilitiesInitializer"/> lives here.
/// </para>
/// </summary>
public sealed partial class MauiErrorHandlingInitializer : IMauiInitializeService
{
    /// <summary>Source tag handed to the callback for an exception that reached the CLR's last-chance handler.</summary>
    public const string AppDomainSource = "AppDomain";

    /// <summary>Source tag handed to the callback for a faulted task whose exception nobody observed.</summary>
    public const string TaskSchedulerSource = "TaskScheduler";

    private const string LoggerCategory = "MMCA.Common.UI.Maui.UnhandledException";

    // Both events are process-wide statics, so the guard has to be static too: a head that calls
    // the builder extension twice would otherwise report every crash twice.
    private static readonly Lock HookSync = new();
    private static bool _hooked;

    private readonly Action<Exception, string>? _onUnhandled;

    private ILogger? _logger;

    /// <summary>Creates the initializer.</summary>
    /// <param name="onUnhandled">
    /// Optional crash-reporter hook, invoked with the exception and one of
    /// <see cref="AppDomainSource"/> / <see cref="TaskSchedulerSource"/>.
    /// </param>
    public MauiErrorHandlingInitializer(Action<Exception, string>? onUnhandled = null) =>
        _onUnhandled = onUnhandled;

    /// <inheritdoc />
    public void Initialize(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // GetService, not GetRequiredService: a head that configured no logging still gets the
        // handlers (and the callback), it just has nowhere to write the report.
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);

        lock (HookSync)
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            // ExceptionObject is typed as object because the runtime can surface a throw that is
            // not a CLR exception at all. Substituting a stand-in keeps the report shape uniform
            // rather than making every consumer of the callback handle a second case.
            var exception = e.ExceptionObject as Exception
                ?? new InvalidOperationException("The runtime raised a throw that is not a CLR exception.");

            Report(exception, AppDomainSource);
        }
#pragma warning disable CA1031 // Do not catch general exception types - a crash reporter that throws replaces one crash with a worse one
        catch
#pragma warning restore CA1031
        {
            // Deliberately silent. This is the CLR's last-chance path: there is no outer handler
            // left to tell, and the logger is the very thing that may have just failed.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            // FIRST, ahead of anything that can throw: an unobserved task exception is re-raised
            // from the finalizer thread, so marking it observed here is what keeps a faulted
            // fire-and-forget task from killing the process at the next collection.
            e.SetObserved();

            Exception? faulted = e.Exception;
            Report(faulted ?? new InvalidOperationException("A faulted task carried no exception detail."), TaskSchedulerSource);
        }
#pragma warning disable CA1031 // Do not catch general exception types - see above
        catch
#pragma warning restore CA1031
        {
            // Same reasoning: the observation above already happened, which is the part that
            // matters for keeping the process alive.
        }
    }

    private void Report(Exception exception, string source)
    {
        if (_logger is not null)
        {
            LogUnhandled(_logger, source, exception);
        }

        _onUnhandled?.Invoke(exception, source);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception surfaced by {Source}")]
    private static partial void LogUnhandled(ILogger logger, string source, Exception exception);
}
