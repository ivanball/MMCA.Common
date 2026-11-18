using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class OutboxSettingsTests
{
    [Fact]
    public void SectionName_IsOutbox() =>
        OutboxSettings.SectionName.Should().Be("Outbox");

    [Theory]
    [InlineData(nameof(OutboxSettings.BatchSize), 50)]
    [InlineData(nameof(OutboxSettings.MaxRetries), 5)]
    [InlineData(nameof(OutboxSettings.PollingIntervalSeconds), 2)]
    [InlineData(nameof(OutboxSettings.ProcessingDelaySeconds), 0)]
    [InlineData(nameof(OutboxSettings.DataSource), DataSource.SQLServer)]
    public void Default_PropertyHasTheDocumentedValue(string property, object? expected) =>
        typeof(OutboxSettings).GetProperty(property)!.GetValue(new OutboxSettings()).Should().Be(expected);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new OutboxSettings
        {
            BatchSize = 100,
            MaxRetries = 10,
            PollingIntervalSeconds = 30,
            ProcessingDelaySeconds = 60,
            DataSource = DataSource.Sqlite,
        };

        sut.BatchSize.Should().Be(100);
        sut.MaxRetries.Should().Be(10);
        sut.PollingIntervalSeconds.Should().Be(30);
        sut.ProcessingDelaySeconds.Should().Be(60);
        sut.DataSource.Should().Be(DataSource.Sqlite);
    }
}
