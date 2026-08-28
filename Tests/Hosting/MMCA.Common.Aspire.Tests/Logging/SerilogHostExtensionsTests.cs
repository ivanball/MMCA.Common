using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Logging;
using Serilog;
using Serilog.Events;

namespace MMCA.Common.Aspire.Tests.Logging;

/// <summary>
/// The shared host-logging bootstrap. Its two decisions carry real operational weight, so both are
/// asserted directly: the minimum level (Debug only in Development) and whether the rolling file sink
/// applies (everywhere except Production, where it duplicated every event onto ephemeral container
/// disk that nothing reads). The provider registration is asserted too, because
/// <c>builder.Logging.AddSerilog</c> rather than <c>UseSerilog</c> is what keeps the OpenTelemetry
/// provider alive alongside Serilog.
/// </summary>
public sealed class SerilogHostExtensionsTests
{
    private static StubHostEnvironment Environment(string environmentName) =>
        new() { EnvironmentName = environmentName };

    [Theory]
    [InlineData("Development", LogEventLevel.Debug)]
    [InlineData("Staging", LogEventLevel.Information)]
    [InlineData("Production", LogEventLevel.Information)]
    public void MinimumLevel_IsDebugOnlyInDevelopment(string environmentName, LogEventLevel expected) =>
        SerilogHostExtensions.ResolveMinimumLevel(Environment(environmentName)).Should().Be(expected);

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    public void FileSink_AppliesEverywhereExceptProduction(string environmentName, bool expected) =>
        SerilogHostExtensions.ShouldWriteFileSink(Environment(environmentName)).Should().Be(expected);

    [Fact]
    public void DevelopmentLogger_EmitsDebugAndWritesTheFileSink()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var logger = SerilogHostExtensions
                .CreateLoggerConfiguration(Environment(Environments.Development), Path.Combine(directory, "svc.txt"))
                .CreateLogger();

            logger.IsEnabled(LogEventLevel.Debug).Should().BeTrue();
            logger.Information("hello");
            logger.Dispose();

            Directory.EnumerateFiles(directory, "svc*.txt").Should().NotBeEmpty(
                "outside Production the rolling file is what a local or CI E2E failure is diagnosed from");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProductionLogger_DropsDebugAndWritesNoFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var logger = SerilogHostExtensions
                .CreateLoggerConfiguration(Environment(Environments.Production), Path.Combine(directory, "svc.txt"))
                .CreateLogger();

            logger.IsEnabled(LogEventLevel.Debug).Should().BeFalse();
            logger.Information("hello");
            logger.Dispose();

            Directory.EnumerateFiles(directory).Should().BeEmpty(
                "stdout plus the OTel exporter already carry production logs; the file sink only filled ephemeral disk");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FrameworkNoise_IsCappedAtWarning()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var logger = SerilogHostExtensions
                .CreateLoggerConfiguration(Environment(Environments.Development), Path.Combine(directory, "svc.txt"))
                .CreateLogger();

            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Microsoft.EntityFrameworkCore.Database.Command")
                .IsEnabled(LogEventLevel.Information).Should().BeFalse();
            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics")
                .IsEnabled(LogEventLevel.Information).Should().BeFalse();
            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "MMCA.Common.Application")
                .IsEnabled(LogEventLevel.Debug).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigureHook_RunsAfterTheDefaults()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var logger = SerilogHostExtensions
                .CreateLoggerConfiguration(
                    Environment(Environments.Development),
                    Path.Combine(directory, "svc.txt"),
                    configuration => configuration.MinimumLevel.Error())
                .CreateLogger();

            logger.IsEnabled(LogEventLevel.Debug).Should().BeFalse(
                "the hook is the per-host extension point, so it must be able to override a default");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BlankLogFilePath_IsRejected()
    {
        var act = () => SerilogHostExtensions.CreateLoggerConfiguration(Environment(Environments.Development), "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddCommonSerilog_RegistersOneProviderAndPublishesTheGlobalLogger()
    {
        var directory = CreateTempDirectory();
        var previous = Log.Logger;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });

            var returned = builder.AddCommonSerilog(Path.Combine(directory, "svc.txt"));

            returned.Should().BeSameAs(builder);
            Log.Logger.IsEnabled(LogEventLevel.Debug).Should().BeTrue(
                "AddCommonSerilog publishes the configured logger as the global Log.Logger the hosts rely on");
            using var provider = builder.Services.BuildServiceProvider();
            provider.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>()
                .Should().ContainSingle(p => p is Serilog.Extensions.Logging.SerilogLoggerProvider,
                    "Serilog must be added as ONE provider alongside the others: UseSerilog would replace the whole ILoggerFactory and silence the OpenTelemetry provider AddServiceDefaults adds");
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mmca-serilog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
