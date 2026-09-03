using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// Tests for <see cref="PhoneNumberValueConverter"/> and
/// <see cref="NullablePhoneNumberValueConverter"/>.
/// </summary>
public sealed class PhoneNumberValueConverterTests
{
    private const string Sample = "+1 404 555 0134";

    // ── Non-nullable converter ──
    [Fact]
    public void Converter_RoundTrips_PhoneNumberThroughItsStringValue()
    {
        var converter = new PhoneNumberValueConverter();
        var phoneNumber = PhoneNumber.Create($"  {Sample}  ").Value!;

        string stored = converter.ConvertToProviderExpression.Compile()(phoneNumber);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(Sample, "PhoneNumber trims at creation time");
        read.Should().Be(phoneNumber);
    }

    [Fact]
    public void Converter_MapsToAndFromString_SoTheColumnStaysAPlainStringColumn()
    {
        var converter = new PhoneNumberValueConverter();

        converter.ModelClrType.Should().Be<PhoneNumber>();
        converter.ProviderClrType.Should().Be<string>();
    }

    // ── Nullable converter ──
    [Fact]
    public void NullableConverter_RoundTripsAValue()
    {
        var converter = new NullablePhoneNumberValueConverter();
        var phoneNumber = PhoneNumber.Create(Sample).Value!;

        string? stored = converter.ConvertToProviderExpression.Compile()(phoneNumber);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(Sample);
        read.Should().Be(phoneNumber);
    }

    [Fact]
    public void NullableConverter_PassesNullThroughBothLegs()
    {
        var converter = new NullablePhoneNumberValueConverter();

        converter.ConvertToProviderExpression.Compile()(null).Should().BeNull();
        converter.ConvertFromProviderExpression.Compile()(null).Should().BeNull(
            "an absent phone number must stay a NULL column value, not an empty string or a failed Create");
    }
}
