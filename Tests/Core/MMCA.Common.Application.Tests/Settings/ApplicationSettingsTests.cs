using System.ComponentModel.DataAnnotations;
using System.Reflection;
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
    public void MaxExportRows_DefaultsTo100000() =>
        new ApplicationSettings().MaxExportRows.Should().Be(100_000);

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
    public void MaxExportRows_CanBeSet()
    {
        var settings = new ApplicationSettings { MaxExportRows = 250 };

        settings.MaxExportRows.Should().Be(250);
    }

    [Fact]
    public void MaxExportRows_CarriesARangeAttribute()
    {
        RangeAttribute? range = typeof(ApplicationSettings)
            .GetProperty(nameof(ApplicationSettings.MaxExportRows))!
            .GetCustomAttribute<RangeAttribute>();

        range.Should().NotBeNull(because: "a host that opts into data-annotations validation must reject a nonsensical cap");
        range!.Minimum.Should().Be(1);
        range.Maximum.Should().Be(10_000_000);
    }

    [Fact]
    public void DatabaseInitStrategy_CanBeSet()
    {
        var settings = new ApplicationSettings { DatabaseInitStrategy = "None" };

        settings.DatabaseInitStrategy.Should().Be("None");
    }

}
