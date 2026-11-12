using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Health;

namespace MMCA.Common.Aspire.Tests.Health;

/// <summary>
/// SEC-Common-71 and SEC-ADC-17: the anonymous, rate-limit-exempt <c>/health</c> and
/// <c>/health/ready</c> endpoints must not fan out every dependency probe per request. One probe
/// round per cache window, with single flight, is what bounds the amplification the bypass list
/// cannot.
/// </summary>
public sealed class CachedHealthReportProviderTests
{
    /// <summary>Counts how often it is actually asked, which is the whole subject of the test.</summary>
    private sealed class CountingCheck : IHealthCheck
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public TaskCompletionSource? Gate { get; set; }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);

            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            return HealthCheckResult.Healthy();
        }
    }

    /// <summary>Manually advanced clock, so the cache window is exercised without waiting.</summary>
    private sealed class StubClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    /// <summary>Healthy until told to hang; then waits until its token is cancelled.</summary>
    private sealed class HangingCheck : IHealthCheck
    {
        private int _started;

        public bool Hangs { get; set; }

        public int Started => Volatile.Read(ref _started);

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!Hangs)
            {
                return HealthCheckResult.Healthy();
            }

            Interlocked.Increment(ref _started);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
    }

    private static (CachedHealthReportProvider Provider, CountingCheck Check, StubClock Clock, ServiceProvider Services)
        Build(int cacheSeconds)
    {
        var check = new CountingCheck();
        var clock = new StubClock();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCheck("counted", check);
        var provider = services.BuildServiceProvider();

        var sut = new CachedHealthReportProvider(
            provider.GetRequiredService<HealthCheckService>(),
            Options.Create(new HealthReportCacheOptions { CacheSeconds = cacheSeconds }),
            clock);

        return (sut, check, clock, provider);
    }

    [Fact]
    public void Options_DefaultToAShortNonZeroWindow()
    {
        var options = new HealthReportCacheOptions();

        options.CacheSeconds.Should().BeGreaterThan(0);
        HealthReportCacheOptions.SectionName.Should().Be("HealthChecks");
    }

    [Fact]
    public async Task RepeatedRequestsInsideTheWindow_RunTheProbesOnce()
    {
        var (sut, check, _, services) = Build(cacheSeconds: 5);
        await using var scope = services;

        for (var i = 0; i < 50; i++)
        {
            var report = await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
            report.Status.Should().Be(HealthStatus.Healthy);
        }

        check.Invocations.Should().Be(1, "50 anonymous hits must not become 50 probe rounds");
    }

    [Fact]
    public async Task OnceTheWindowElapses_TheProbesRunAgain()
    {
        var (sut, check, clock, services) = Build(cacheSeconds: 5);
        await using var scope = services;

        await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(6));
        await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);

        check.Invocations.Should().Be(2, "a real outage must still be noticed within a probe interval");
    }

    [Fact]
    public async Task TwoEndpoints_DoNotShareOneCachedReport()
    {
        var (sut, check, _, services) = Build(cacheSeconds: 5);
        await using var scope = services;

        await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
        await sut.GetReportAsync("/health/ready", predicate: null, TestContext.Current.CancellationToken);

        check.Invocations.Should().Be(2, "the full report and the readiness subset run different check sets");
    }

    [Fact]
    public async Task ConcurrentColdRequests_ShareOneProbeRound()
    {
        var (sut, check, _, services) = Build(cacheSeconds: 5);
        await using var scope = services;

        check.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);

        // Give the first caller time to take the refresh slot before the others pile in.
        while (check.Invocations == 0)
        {
            await Task.Yield();
        }

        var followers = Enumerable.Range(0, 10)
            .Select(_ => sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken))
            .ToList();

        check.Gate.SetResult();
        await Task.WhenAll([first, .. followers]);

        check.Invocations.Should().Be(1, "single flight means one probe round, not one per waiting caller");
    }

    [Fact]
    public void Options_DefaultToAFiveSecondCheckTimeout() =>
        new HealthReportCacheOptions().CheckTimeoutSeconds.Should().Be(5);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void CheckTimeoutSeconds_IsRangeValidated(int seconds, bool valid)
    {
        var options = new HealthReportCacheOptions { CheckTimeoutSeconds = seconds };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
            results,
            validateAllProperties: true);

        isValid.Should().Be(valid);
    }

    // Ruling "health checks are bounded": a check that never finishes on its own is cut off by its
    // registration timeout and reported failed, and while that one round runs the cache serves the
    // previous report at most for that round.
    [Fact]
    public async Task AHangingCheck_IsReportedFailedAfterItsTimeout_AndTheStaleReportLastsOneRound()
    {
        var hang = new HangingCheck();
        var clock = new StubClock();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "hanging",
            hang,
            failureStatus: null,
            tags: null,
            timeout: TimeSpan.FromSeconds(1)));
        await using var provider = services.BuildServiceProvider();
        using var sut = new CachedHealthReportProvider(
            provider.GetRequiredService<HealthCheckService>(),
            Options.Create(new HealthReportCacheOptions { CacheSeconds = 5 }),
            clock);

        (await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken))
            .Status.Should().Be(HealthStatus.Healthy);

        hang.Hangs = true;
        clock.Advance(TimeSpan.FromSeconds(6));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var refreshing = sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
        while (hang.Started == 0)
        {
            await Task.Yield();
        }

        var duringTheRound = await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
        var refreshed = await refreshing.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        elapsed.Stop();
        var afterTheRound = await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);

        duringTheRound.Status.Should().Be(HealthStatus.Healthy, "the one round in progress serves the previous report");
        refreshed.Status.Should().Be(HealthStatus.Unhealthy);
        refreshed.Entries["hanging"].Description.Should().Contain("timeout");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the check timeout bounds the round");
        afterTheRound.Status.Should().Be(HealthStatus.Unhealthy, "no stale report survives the round that timed out");
    }

    [Fact]
    public async Task WithCachingDisabled_EveryRequestProbes()
    {
        var (sut, check, _, services) = Build(cacheSeconds: 0);
        await using var scope = services;

        await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);
        await sut.GetReportAsync("/health", predicate: null, TestContext.Current.CancellationToken);

        check.Invocations.Should().Be(2);
    }
}
