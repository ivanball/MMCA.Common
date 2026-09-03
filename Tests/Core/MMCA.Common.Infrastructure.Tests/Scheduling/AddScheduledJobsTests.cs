using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Coverage for the scheduler's DI surface: <c>AddScheduledJobs</c> is called once per host and
/// <c>AddScheduledJob&lt;TJob&gt;</c> any number of times, in any order, and both are idempotent.
/// </summary>
public sealed class AddScheduledJobsTests
{
    // ── Jobs accumulate across modules ──
    [Fact]
    public void AddScheduledJob_TwoDifferentJobs_BothResolve()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<FirstJob>();
        services.AddScheduledJob<SecondJob>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IScheduledJob>()
            .Select(job => job.Name)
            .Should().BeEquivalentTo("first", "second");
    }

    // ── The same job registered twice is registered once ──
    [Fact]
    public void AddScheduledJob_SameJobTwice_RegistersItOnce()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<FirstJob>();
        services.AddScheduledJob<FirstJob>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IScheduledJob>().Should().ContainSingle();
        scope.ServiceProvider.GetService<FirstJob>().Should().NotBeNull(
            "the concrete type is registered too, so a test or an admin endpoint can run the job on demand");
    }

    // ── The runner is registered exactly once, however many times the host opts in ──
    [Fact]
    public void AddScheduledJobs_CalledTwice_RegistersExactlyOneRunner()
    {
        var services = new ServiceCollection();
        services.AddScheduledJobs(EmptyConfiguration());
        services.AddScheduledJobs(EmptyConfiguration());

        services.Count(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(ScheduledJobRunner))
            .Should().Be(1, "two runners in one process would race for the same job rows");
    }

    // ── Order-free: jobs before the opt-in, or after it, give the same container ──
    [Fact]
    public void AddScheduledJobs_JobsRegisteredBeforeTheOptIn_StillResolve()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<FirstJob>();
        services.AddScheduledJobs(EmptyConfiguration());
        services.AddScheduledJob<SecondJob>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IScheduledJob>().Should().HaveCount(2);
    }

    // ── Settings binding ──
    [Fact]
    public void AddScheduledJobs_BindsTheSchedulerSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Scheduler:Enabled"] = "true",
                ["Scheduler:PollingIntervalSeconds"] = "45",
                ["Scheduler:LeaseSeconds"] = "120",
                ["Scheduler:Jobs:first:Cron"] = "*/2 * * * *",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScheduledJobs(configuration);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<SchedulerSettings>>().Value;

        settings.Enabled.Should().BeTrue();
        settings.PollingIntervalSeconds.Should().Be(45);
        settings.LeaseSeconds.Should().Be(120);
        settings.Jobs.Should().ContainKey("first");
        settings.Jobs["first"].Cron.Should().Be("*/2 * * * *");
    }

    // ── Defaults ──
    [Fact]
    public void SchedulerSettings_Defaults_AreOffWithA30SecondPollAnd300SecondLease()
    {
        var settings = new SchedulerSettings();

        settings.Enabled.Should().BeFalse("adopting the framework must never add a table or a loop unasked");
        settings.PollingIntervalSeconds.Should().Be(30);
        settings.LeaseSeconds.Should().Be(300);
        settings.Jobs.Should().BeEmpty();
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private sealed class FirstJob : IScheduledJob
    {
        public string Name => "first";

        public string CronExpression => "0 * * * *";

        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SecondJob : IScheduledJob
    {
        public string Name => "second";

        public string CronExpression => "0 0 * * *";

        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
