using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Services.Diagnostics;

namespace MMCA.Common.UI.Tests.Services.Diagnostics;

/// <summary>
/// The in-app diagnostics ring buffer: it must stay bounded (a long-running app keeps logging) and
/// keep the order the entries were written, newest last, across the wrap-around.
/// </summary>
public sealed class InAppLogBufferTests
{
    [Fact]
    public void DefaultCapacity_Is500()
    {
        var buffer = new InAppLogBuffer(Options.Create(new InAppDiagnosticsOptions()));

        buffer.Capacity.Should().Be(500);
    }

    [Fact]
    public void Snapshot_ReturnsEntriesOldestFirstNewestLast()
    {
        var buffer = CreateBuffer(capacity: 5);

        buffer.Add(Entry("one"));
        buffer.Add(Entry("two"));
        buffer.Add(Entry("three"));

        buffer.Snapshot().Select(e => e.Message).Should().Equal("one", "two", "three");
    }

    [Fact]
    public void Add_PastCapacity_DropsTheOldestAndKeepsTheOrder()
    {
        var buffer = CreateBuffer(capacity: 3);

        for (var i = 1; i <= 7; i++)
        {
            buffer.Add(Entry("m" + i.ToString(CultureInfo.InvariantCulture)));
        }

        buffer.Count.Should().Be(3);
        buffer.Snapshot().Select(e => e.Message).Should().Equal("m5", "m6", "m7");
    }

    [Fact]
    public void Clear_EmptiesTheBufferAndItRefillsInOrder()
    {
        var buffer = CreateBuffer(capacity: 2);
        buffer.Add(Entry("a"));
        buffer.Add(Entry("b"));
        buffer.Add(Entry("c"));

        buffer.Clear();
        buffer.Snapshot().Should().BeEmpty();

        buffer.Add(Entry("d"));
        buffer.Snapshot().Select(e => e.Message).Should().Equal("d");
    }

    [Fact]
    public void ToText_OneLinePerEntry_NewestLast_WithTheExceptionOnItsOwnLine()
    {
        var buffer = CreateBuffer(capacity: 5);
        buffer.Add(new InAppLogEntry(
            new DateTimeOffset(2026, 11, 7, 10, 30, 0, TimeSpan.Zero), LogLevel.Information, "App.Sync", "Started", null));
        buffer.Add(new InAppLogEntry(
            new DateTimeOffset(2026, 11, 7, 10, 30, 1, TimeSpan.Zero), LogLevel.Error, "App.Sync", "Failed", "System.TimeoutException: Slow"));

        var lines = buffer.ToText().Split(Environment.NewLine);

        lines.Should().Equal(
            "2026-11-07 10:30:00.000Z [Information] App.Sync: Started",
            "2026-11-07 10:30:01.000Z [Error] App.Sync: Failed",
            "    System.TimeoutException: Slow");
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
    {
        var act = () => CreateBuffer(capacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    internal static InAppLogEntry Entry(string message) =>
        new(DateTimeOffset.UnixEpoch, LogLevel.Information, "Test", message, null);

    private static InAppLogBuffer CreateBuffer(int capacity) =>
        new(Options.Create(new InAppDiagnosticsOptions { Capacity = capacity }));
}
