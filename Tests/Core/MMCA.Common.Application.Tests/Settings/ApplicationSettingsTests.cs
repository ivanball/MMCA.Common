using AwesomeAssertions;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Tests.Settings;

public sealed class ApplicationSettingsTests
{
    // ── Defaults ──
    [Fact]
    public void UseMiniProfiler_DefaultsToFalse() =>
        new ApplicationSettings().UseMiniProfiler.Should().BeFalse();

    [Fact]
    public void MaxPageSize_DefaultsTo500() =>
        new ApplicationSettings().MaxPageSize.Should().Be(500);

    [Fact]
    public void DatabaseInitStrategy_DefaultsToMigrate() =>
        new ApplicationSettings().DatabaseInitStrategy.Should().Be("Migrate");

    // ── SectionName ──
    [Fact]
    public void SectionName_IsApplicationSettings() =>
        ApplicationSettings.SectionName.Should().Be("ApplicationSettings");

    // ── Init properties ──
    [Fact]
    public void UseMiniProfiler_CanBeSet()
    {
        var settings = new ApplicationSettings { UseMiniProfiler = true };

        settings.UseMiniProfiler.Should().BeTrue();
    }

    [Fact]
    public void MaxPageSize_CanBeSet()
    {
        var settings = new ApplicationSettings { MaxPageSize = 100 };

        settings.MaxPageSize.Should().Be(100);
    }

    [Fact]
    public void DatabaseInitStrategy_CanBeSet()
    {
        var settings = new ApplicationSettings { DatabaseInitStrategy = "None" };

        settings.DatabaseInitStrategy.Should().Be("None");
    }

    // ── Implements interface ──
    [Fact]
    public void ImplementsIApplicationSettings() =>
        new ApplicationSettings().Should().BeAssignableTo<IApplicationSettings>();
}
