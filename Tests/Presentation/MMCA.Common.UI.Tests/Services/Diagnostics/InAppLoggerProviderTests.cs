using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Services.Diagnostics;

namespace MMCA.Common.UI.Tests.Services.Diagnostics;

/// <summary>
/// The in-app diagnostics logger as a host registers it (<c>AddInAppDiagnostics</c> plus the normal
/// logging pipeline): the level filter (Information and above by default), what one entry records,
/// and that nothing is registered unless the host asks.
/// </summary>
public sealed class InAppLoggerProviderTests
{
    [Fact]
    public void ByDefault_RecordsInformationAndAbove_AndDropsDebugAndTrace()
    {
        using var provider = BuildProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Sync");

        logger.LogTrace("trace");
        logger.LogDebug("debug");
        logger.LogInformation("info");
        logger.LogWarning("warning");
        logger.LogError("error");
        logger.LogCritical("critical");

        provider.GetRequiredService<InAppLogBuffer>().Snapshot()
            .Select(e => e.Message)
            .Should().Equal("info", "warning", "error", "critical");
    }

    [Fact]
    public void MinimumLevel_IsConfigurable()
    {
        using var provider = BuildProvider(o => o.MinimumLevel = LogLevel.Warning);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Sync");

        logger.LogInformation("info");
        logger.LogWarning("warning");

        provider.GetRequiredService<InAppLogBuffer>().Snapshot()
            .Select(e => e.Message)
            .Should().Equal("warning");
    }

    [Fact]
    public void Capacity_IsConfigurable_AndBoundsTheLog()
    {
        using var provider = BuildProvider(o => o.Capacity = 2);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Sync");

        logger.LogInformation("one");
        logger.LogInformation("two");
        logger.LogInformation("three");

        provider.GetRequiredService<InAppLogBuffer>().Snapshot()
            .Select(e => e.Message)
            .Should().Equal("two", "three");
    }

    [Fact]
    public void Entry_RecordsTimestampLevelCategoryMessageAndTheExceptionMessages()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        using var provider = BuildProvider(timeProvider: clock);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Sync");

        logger.LogError(
            new InvalidOperationException("Outer failure", new TimeoutException("Inner timeout")),
            "Sync of {Count} items failed",
            3);

        var entry = provider.GetRequiredService<InAppLogBuffer>().Snapshot().Should().ContainSingle().Subject;
        entry.Timestamp.Should().Be(clock.GetUtcNow());
        entry.Level.Should().Be(LogLevel.Error);
        entry.Category.Should().Be("App.Sync");
        entry.Message.Should().Be("Sync of 3 items failed");
        entry.Exception.Should().Be(
            "System.InvalidOperationException: Outer failure ---> System.TimeoutException: Inner timeout");
    }

    [Fact]
    public void CalledTwice_RecordsEachEntryOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInAppDiagnostics();
        services.AddInAppDiagnostics();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILoggerFactory>().CreateLogger("App").LogInformation("once");

        provider.GetRequiredService<InAppLogBuffer>().Snapshot().Should().ContainSingle();
    }

    [Fact]
    public void WithoutAddInAppDiagnostics_NothingIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        provider.GetService<InAppLogBuffer>().Should().BeNull();
        provider.GetServices<ILoggerProvider>().OfType<InAppLoggerProvider>().Should().BeEmpty();
    }

    private static ServiceProvider BuildProvider(
        Action<InAppDiagnosticsOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();

        // The logging pipeline's own floor is set to Trace so the provider's filter is the one under test.
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddInAppDiagnostics(configure);
        return services.BuildServiceProvider();
    }
}
