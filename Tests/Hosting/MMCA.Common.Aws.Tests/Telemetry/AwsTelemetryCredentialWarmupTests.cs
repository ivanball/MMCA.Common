using Amazon.Runtime;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Aws.Telemetry;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// Rulings M1 and M2: the AWS exporter warms its default-chain credential while the host starts, as
/// the Google twin warms its token, so the first export already finds it. The warm-up resolves once,
/// never fails the host, and on failure logs one Warning naming the exception type and message.
/// </summary>
public sealed class AwsTelemetryCredentialWarmupTests
{
    [Fact]
    public async Task StartAsync_ResolvesTheCredentialOnce()
    {
        var source = new CountingSource(() => new BasicAWSCredentials("AKIDEXAMPLE", "secret"));
        var logger = new ListLogger<AwsTelemetryCredentialWarmup>();
        var warmup = new AwsTelemetryCredentialWarmup(source, logger);

        await warmup.StartAsync(TestContext.Current.CancellationToken);
        await warmup.StopAsync(TestContext.Current.CancellationToken);

        source.Calls.Should().Be(1);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailingChain_DoesNotFailTheStart_AndLogsOneWarningWithTheExceptionType()
    {
        var source = new CountingSource(() => throw new AmazonClientException("no credential in the chain"));
        var logger = new ListLogger<AwsTelemetryCredentialWarmup>();
        var warmup = new AwsTelemetryCredentialWarmup(source, logger);

        var start = () => warmup.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync();
        source.Calls.Should().Be(1);
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain(nameof(AmazonClientException)).And.Contain("no credential in the chain");
    }

    [Fact]
    public async Task NoCredential_LogsOneWarning()
    {
        var source = new CountingSource(() => null);
        var logger = new ListLogger<AwsTelemetryCredentialWarmup>();
        var warmup = new AwsTelemetryCredentialWarmup(source, logger);

        await warmup.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task AFailingChain_DoesNotStopTheHost()
    {
        var source = new CountingSource(() => throw new AmazonClientException("no credential in the chain"));
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(provider => new AwsTelemetryCredentialWarmup(
            source,
            provider.GetRequiredService<ILogger<AwsTelemetryCredentialWarmup>>()));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        source.Calls.Should().Be(1);
    }

    private sealed class CountingSource(Func<AWSCredentials?> resolve) : IAwsTelemetryCredentialSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<AWSCredentials?> GetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return ValueTask.FromResult(resolve());
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<AWSCredentials?>(ex);
            }
        }

        public AWSCredentials? Get(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return resolve();
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
