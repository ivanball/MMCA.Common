using System.Globalization;
using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// The wire conventions the hand-written gRPC adapters and services shared by copy (Store
/// <c>UserSalesExportGrpcService</c>, <c>UserCatalogExportGrpcService</c>,
/// <c>UserSalesExportServiceGrpcAdapter</c>, <c>UserCatalogExportServiceGrpcAdapter</c>,
/// <c>ProductVariantServiceGrpcAdapter</c>; ADC <c>UserNotificationExportGrpcService</c>,
/// <c>UserEngagementExportGrpcService</c> and their adapters). Ported from the ADC export tests:
/// SQL Server hands back <see cref="DateTimeKind.Unspecified"/> values that "O" would emit without
/// the Z marker, and a rolling deploy can put a marker-less peer on the other end, so both forms
/// must land as <see cref="DateTimeKind.Utc"/> on the same instant. Money travels as an invariant
/// decimal string so nothing rounds it and no comma-decimal locale misreads it.
/// </summary>
public sealed class GrpcWireFormatTests
{
    private const string WithMarker = "2026-05-03T09:00:00.0000000Z";
    private const string WithoutMarker = "2026-05-03T09:00:00.0000000";

    // -- FormatUtc --
    [Fact]
    public void FormatUtc_UnspecifiedKind_EmitsTheZMarkerWithoutShiftingTheInstant()
    {
        var value = new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Unspecified);

        GrpcWireFormat.FormatUtc(value).Should().Be(WithMarker);
    }

    [Fact]
    public void FormatUtc_UtcKind_KeepsTheSameInstant()
    {
        var value = new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Utc);

        GrpcWireFormat.FormatUtc(value).Should().Be(WithMarker);
    }

    [Fact]
    public void FormatUtc_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            GrpcWireFormat.FormatUtc(new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Utc)).Should().Be(WithMarker);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatUtcOrEmpty_Null_IsTheEmptyString() =>
        GrpcWireFormat.FormatUtcOrEmpty(null).Should().BeEmpty();

    [Fact]
    public void FormatUtcOrEmpty_Value_FormatsLikeFormatUtc()
    {
        var value = new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Unspecified);

        GrpcWireFormat.FormatUtcOrEmpty(value).Should().Be(WithMarker);
    }

    // -- ParseUtc --
    [Theory]
    [InlineData(WithMarker)]
    [InlineData(WithoutMarker)]
    public void ParseUtc_EitherWireForm_IsUtcOnTheSameInstant(string wire)
    {
        DateTime parsed = GrpcWireFormat.ParseUtc(wire);

        parsed.Kind.Should().Be(DateTimeKind.Utc);
        parsed.Should().Be(new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseUtc_RoundTripsFormatUtc()
    {
        var value = new DateTime(2026, 5, 3, 9, 0, 0, 123, DateTimeKind.Unspecified).AddTicks(4567);

        GrpcWireFormat.ParseUtc(GrpcWireFormat.FormatUtc(value)).Ticks.Should().Be(value.Ticks);
    }

    [Fact]
    public void ParseUtc_Malformed_ThrowsFormatException()
    {
        Action act = () => GrpcWireFormat.ParseUtc("not-a-date");

        act.Should().Throw<FormatException>("the adapters surface a corrupt peer payload as a fault, as DateTime.Parse did");
    }

    [Fact]
    public void ParseUtcOrNull_Empty_IsNull() =>
        GrpcWireFormat.ParseUtcOrNull(string.Empty).Should().BeNull();

    [Theory]
    [InlineData(WithMarker)]
    [InlineData(WithoutMarker)]
    public void ParseUtcOrNull_Value_ParsesLikeParseUtc(string wire)
    {
        DateTime? parsed = GrpcWireFormat.ParseUtcOrNull(wire);

        parsed.Should().Be(new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Utc));
        parsed!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    // -- Decimal --
    [Fact]
    public void FormatDecimal_IsInvariantAndKeepsScale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            GrpcWireFormat.FormatDecimal(1234.50m).Should().Be("1234.50");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseDecimal_IsInvariantUnderACommaDecimalCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            GrpcWireFormat.ParseDecimal("1234.50").Should().Be(1234.50m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseDecimal_RoundTripsFormatDecimal() =>
        GrpcWireFormat.ParseDecimal(GrpcWireFormat.FormatDecimal(-0.0001m)).Should().Be(-0.0001m);

    [Theory]
    [InlineData("")]
    [InlineData("12,34,x")]
    [InlineData("1e5")]
    public void ParseDecimal_Malformed_ThrowsFormatException(string wire)
    {
        Action act = () => GrpcWireFormat.ParseDecimal(wire);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParseDecimal_Valid_ReturnsTrueWithTheValue()
    {
        GrpcWireFormat.TryParseDecimal("19.99", out var value).Should().BeTrue();
        value.Should().Be(19.99m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e5")]
    public void TryParseDecimal_Malformed_ReturnsFalseForTheAdapterToMap(string? wire)
    {
        GrpcWireFormat.TryParseDecimal(wire, out var value).Should().BeFalse();
        value.Should().Be(0m);
    }
}
