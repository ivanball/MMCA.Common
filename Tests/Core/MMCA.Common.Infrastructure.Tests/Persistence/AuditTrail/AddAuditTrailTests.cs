using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// Coverage for the audit trail's DI surface: what <c>AddAuditTrail</c> registers, that calling it
/// twice is harmless, and that retention rides on the existing scheduler rather than a second loop.
/// </summary>
public sealed class AddAuditTrailTests
{
    [Fact]
    public void AddAuditTrail_BindsTheAuditTrailSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AuditTrail:Enabled"] = "true",
                ["AuditTrail:RetentionDays"] = "365",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAuditTrail(configuration);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<AuditTrailSettings>>().Value;

        settings.Enabled.Should().BeTrue();
        settings.RetentionDays.Should().Be(365);
    }

    [Fact]
    public void AuditTrailSettings_Defaults_AreOffWithNinetyDayRetention()
    {
        var settings = new AuditTrailSettings();

        settings.Enabled.Should().BeFalse("adopting the framework must never add a table or a write unasked");
        settings.RetentionDays.Should().Be(90);
    }

    [Fact]
    public void AddAuditTrail_RegistersTheInterceptorAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddAuditTrail(EmptyConfiguration());

        var descriptor = services.Single(d => d.ServiceType == typeof(AuditTrailSaveChangesInterceptor));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            "per-save state lives in a ConditionalWeakTable keyed by context, so the interceptor is stateless");
    }

    [Fact]
    public void AddAuditTrail_RegistersTheReadSurfaceAsScoped()
    {
        var services = new ServiceCollection();
        services.AddAuditTrail(EmptyConfiguration());

        var descriptor = services.Single(d => d.ServiceType == typeof(IAuditTrailReader));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<AuditTrailReader>();
    }

    [Fact]
    public void AddAuditTrail_RegistersTheRetentionJobWithTheScheduler()
    {
        var services = new ServiceCollection();
        services.AddAuditTrail(EmptyConfiguration());

        services.Count(d => d.ServiceType == typeof(IScheduledJob)
                && d.ImplementationType == typeof(AuditTrailCleanupJob))
            .Should().Be(1, "retention rides on the existing scheduler rather than a second background loop");
    }

    [Fact]
    public void AddAuditTrail_CalledTwice_RegistersEverythingOnce()
    {
        var services = new ServiceCollection();
        services.AddAuditTrail(EmptyConfiguration());
        services.AddAuditTrail(EmptyConfiguration());

        services.Count(d => d.ServiceType == typeof(AuditTrailSaveChangesInterceptor)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IAuditTrailReader)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IScheduledJob)
            && d.ImplementationType == typeof(AuditTrailCleanupJob)).Should().Be(1);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();
}
