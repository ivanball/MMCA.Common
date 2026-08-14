using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// Tests for <see cref="EnumerationValueConverter{TEnumeration}"/> and
/// <see cref="NullableEnumerationValueConverter{TEnumeration}"/>: the packaged replacement for the
/// <c>m =&gt; m.Value, v =&gt; Enumeration&lt;T&gt;.FromValue(v).Value!</c> lambda pair an entity
/// configuration would otherwise hand-roll for every smart enumeration.
/// </summary>
public sealed class EnumerationValueConverterTests
{
    // ── Non-nullable converter ──
    [Fact]
    public void Converter_RoundTrips_MemberThroughItsIntegerValue()
    {
        var converter = new EnumerationValueConverter<Priority>();

        int stored = converter.ConvertToProviderExpression.Compile()(Priority.High);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(3, "the column stores the member's stable integer value");
        read.Should().BeSameAs(Priority.High, "the read leg resolves the interned singleton");
    }

    [Fact]
    public void Converter_MapsToAndFromInt_SoTheColumnStaysAPlainIntColumn()
    {
        var converter = new EnumerationValueConverter<Priority>();

        converter.ModelClrType.Should().Be<Priority>();
        converter.ProviderClrType.Should().Be<int>();
    }

    [Fact]
    public void Converter_ReadLeg_ResolvesEveryDeclaredMember()
    {
        var read = new EnumerationValueConverter<Priority>().ConvertFromProviderExpression.Compile();

        read(1).Should().BeSameAs(Priority.Low);
        read(2).Should().BeSameAs(Priority.Normal);
        read(3).Should().BeSameAs(Priority.High);
    }

    [Fact]
    public void Converter_WriteLeg_MatchesTheHandRolledLambdaItReplaces()
    {
        var converter = new EnumerationValueConverter<Priority>();

        converter.ConvertToProviderExpression.Compile()(Priority.Low).Should().Be(HandRolled(Priority.Low));

        static int HandRolled(Priority p) => p.Value;
    }

    // ── Nullable converter ──
    [Fact]
    public void NullableConverter_RoundTripsAValue()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        int? stored = converter.ConvertToProviderExpression.Compile()(Priority.Low);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(1);
        read.Should().BeSameAs(Priority.Low);
    }

    [Fact]
    public void NullableConverter_PassesNullThroughBothLegs()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        converter.ConvertToProviderExpression.Compile()(null).Should().BeNull();
        converter.ConvertFromProviderExpression.Compile()(null).Should().BeNull(
            "an absent member must stay a NULL column value, not collapse onto the member valued zero");
    }

    [Fact]
    public void NullableConverter_MapsToAndFromNullableInt()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        converter.ModelClrType.Should().Be<Priority>();
        converter.ProviderClrType.Should().Be<int?>();
    }

    private sealed class Priority : Enumeration<Priority>
    {
        public static readonly Priority Low = new(1, "Low");
        public static readonly Priority Normal = new(2, "Normal");
        public static readonly Priority High = new(3, "High");

        private Priority(int value, string name)
            : base(value, name)
        {
        }
    }
}
