using AwesomeAssertions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Domain.Tests.Auth;

/// <summary>
/// The device metadata a native client reports for its session: captured within its column width,
/// blank values stored as nothing, carried to the successor on rotation, and erased with the rest of
/// the client metadata.
/// </summary>
public sealed class RefreshSessionDeviceTests
{
    private static readonly DateTime Now = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DeviceMetadata Pixel = new(
        "3f2a9c1e5b7d4e8f9a0b1c2d3e4f5a6b",
        "Ana's phone",
        "Pixel 8",
        "Google",
        "Android",
        "Phone",
        "Physical");

    [Fact]
    public void RecordDevice_StoresEveryReportedValue()
    {
        var session = NewSession();

        session.RecordDevice(Pixel).IsSuccess.Should().BeTrue();

        session.DeviceId.Should().Be("3f2a9c1e5b7d4e8f9a0b1c2d3e4f5a6b");
        session.DeviceName.Should().Be("Ana's phone");
        session.DeviceModel.Should().Be("Pixel 8");
        session.DeviceManufacturer.Should().Be("Google");
        session.DevicePlatform.Should().Be("Android");
        session.DeviceFormFactor.Should().Be("Phone");
        session.DeviceType.Should().Be("Physical");
    }

    [Fact]
    public void RecordDevice_TruncatesEachValueToTheColumnWidth()
    {
        var session = NewSession();
        var tooLong = new string('x', RefreshSession.DeviceFieldMaxLength + 40);

        session.RecordDevice(new DeviceMetadata(tooLong, tooLong, tooLong, tooLong, tooLong, tooLong, tooLong));

        foreach (var value in AllDeviceFields(session))
        {
            value.Should().HaveLength(RefreshSession.DeviceFieldMaxLength);
        }
    }

    [Fact]
    public void RecordDevice_StoresBlankValuesAsNull_AndTrimsTheRest()
    {
        var session = NewSession();

        session.RecordDevice(new DeviceMetadata("  ", string.Empty, "  Pixel 8  ", null, "\t", "Phone", " "));

        session.DeviceModel.Should().Be("Pixel 8");
        session.DeviceFormFactor.Should().Be("Phone");
        session.DeviceId.Should().BeNull();
        session.DeviceName.Should().BeNull();
        session.DeviceManufacturer.Should().BeNull();
        session.DevicePlatform.Should().BeNull();
        session.DeviceType.Should().BeNull();
    }

    [Fact]
    public void RecordDevice_ReplacesAnEarlierReport_IncludingTheValuesItLeavesOut()
    {
        var session = NewSession();
        session.RecordDevice(Pixel);

        session.RecordDevice(new DeviceMetadata("other-install", null, "iPhone 15", "Apple", "iOS", "Phone", "Physical"));

        session.DeviceId.Should().Be("other-install");
        session.DeviceName.Should().BeNull("a new report describes the device as it is now, not merged with the old one");
        session.DeviceModel.Should().Be("iPhone 15");
    }

    [Fact]
    public void CreateSuccessor_CarriesTheRecordedDevice()
    {
        var session = NewSession();
        session.RecordDevice(Pixel);

        var successor = session.CreateSuccessor("second-token", Now.AddMinutes(15), Now.AddDays(7).AddMinutes(15)).Value!;

        AllDeviceFields(successor).Should().Equal(AllDeviceFields(session));
    }

    [Fact]
    public void CreateSuccessor_OfASessionWithNoDevice_HasNoDevice()
    {
        var successor = NewSession().CreateSuccessor("second-token", Now.AddMinutes(15), Now.AddDays(7).AddMinutes(15)).Value!;

        AllDeviceFields(successor).Should().AllSatisfy(value => value.Should().BeNull());
    }

    [Fact]
    public void Anonymize_ClearsTheDevice()
    {
        var session = NewSession();
        session.RecordDevice(Pixel);

        session.Anonymize().IsSuccess.Should().BeTrue();

        AllDeviceFields(session).Should().AllSatisfy(value => value.Should().BeNull());
        session.IsActiveAt(Now).Should().BeTrue("erasing the metadata does not sign the device out");
    }

    private static RefreshSession NewSession() =>
        RefreshSession.Create(7, "first-token", Now, Now.AddDays(7), "203.0.113.7", "Firefox").Value!;

    private static string?[] AllDeviceFields(RefreshSession session) =>
    [
        session.DeviceId,
        session.DeviceName,
        session.DeviceModel,
        session.DeviceManufacturer,
        session.DevicePlatform,
        session.DeviceFormFactor,
        session.DeviceType,
    ];
}
