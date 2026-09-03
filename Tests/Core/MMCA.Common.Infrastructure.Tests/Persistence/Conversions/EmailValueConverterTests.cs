using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// Tests for <see cref="EmailValueConverter"/> and <see cref="NullableEmailValueConverter"/>: the
/// packaged replacements for the <c>e =&gt; e.Value, v =&gt; Email.Create(v).Value!</c> lambda pair
/// that every consumer entity configuration used to hand-roll.
/// </summary>
public sealed class EmailValueConverterTests
{
    // ── Non-nullable converter ──
    [Fact]
    public void Converter_RoundTrips_EmailThroughItsStringValue()
    {
        var converter = new EmailValueConverter();
        var email = Email.Create("Person@Example.COM").Value!;

        string stored = converter.ConvertToProviderExpression.Compile()(email);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be("person@example.com", "Email normalizes to lowercase at creation time");
        read.Should().Be(email);
    }

    [Fact]
    public void Converter_MapsToAndFromString_SoTheColumnStaysAPlainStringColumn()
    {
        var converter = new EmailValueConverter();

        converter.ModelClrType.Should().Be<Email>();
        converter.ProviderClrType.Should().Be<string>();
    }

    [Fact]
    public void Converter_WriteLeg_MatchesTheHandRolledLambdaItReplaces()
    {
        var converter = new EmailValueConverter();
        var email = Email.Create("a@b.com").Value!;

        converter.ConvertToProviderExpression.Compile()(email).Should().Be(HandRolled(email));

        static string HandRolled(Email e) => e.Value;
    }

    // ── Nullable converter ──
    [Fact]
    public void NullableConverter_RoundTripsAValue()
    {
        var converter = new NullableEmailValueConverter();
        var email = Email.Create("person@example.com").Value!;

        string? stored = converter.ConvertToProviderExpression.Compile()(email);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be("person@example.com");
        read.Should().Be(email);
    }

    [Fact]
    public void NullableConverter_PassesNullThroughBothLegs()
    {
        var converter = new NullableEmailValueConverter();

        converter.ConvertToProviderExpression.Compile()(null).Should().BeNull();
        converter.ConvertFromProviderExpression.Compile()(null).Should().BeNull(
            "an absent email must stay a NULL column value, not an empty string or a failed Create");
    }

    [Fact]
    public void NullableConverter_ReadLeg_MatchesTheHandRolledLambdaItReplaces()
    {
        var converter = new NullableEmailValueConverter();
        var read = converter.ConvertFromProviderExpression.Compile();

        read("person@example.com").Should().Be(HandRolled("person@example.com"));
        read(null).Should().Be(HandRolled(null));

        static Email? HandRolled(string? v) => v == null ? null : Email.Create(v).Value;
    }
}
