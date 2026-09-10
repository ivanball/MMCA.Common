using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Shared.Tests.Identifiers;

/// <summary>
/// The identifier contract itself: equality, the compile-time separation two same-primitive
/// identifiers exist for, parsing through the interface's default <see cref="IParsable{TSelf}"/>
/// implementations, and the reflection helpers the framework plumbing keys off.
/// </summary>
public sealed class StronglyTypedIdTests
{
    [Fact]
    public void From_WrapsThePrimitive()
    {
        OrderId.From(42).Value.Should().Be(42);
        SkuId.From("SKU-1").Value.Should().Be("SKU-1");
    }

    [Fact]
    public void Equality_IsStructural()
    {
        OrderId.From(42).Should().Be(OrderId.From(42));
        (OrderId.From(42) == OrderId.From(42)).Should().BeTrue();
        (OrderId.From(42) != OrderId.From(43)).Should().BeTrue();
        OrderId.From(42).GetHashCode().Should().Be(OrderId.From(42).GetHashCode());
    }

    [Fact]
    public void TwoIdentifiersOverTheSamePrimitive_AreDifferentTypes()
    {
        // The whole point of the wrapper: OrderId and CustomerId are both int underneath, and the
        // compiler still refuses to let one stand in for the other. That is the transposition
        // ADR-085 priced and could not close with the primitive aliases.
        object order = OrderId.From(7);
        object customer = CustomerId.From(7);

        order.Should().NotBe(customer);
        typeof(OrderId).Should().NotBe<CustomerId>();
    }

    [Fact]
    public void Default_IsTheWrappedDefault()
    {
        default(OrderId).Value.Should().Be(0);
        default(SpeakerId).Value.Should().Be(Guid.Empty);
        default(SkuId).Value.Should().BeNull("the wrapper never invents a value the caller did not supply");
    }

    [Fact]
    public void ToString_ShowsTheWrappedValue()
        => OrderId.From(42).ToString().Should().Contain("42");

    [Theory]
    [InlineData("42", true, 42)]
    [InlineData("0", true, 0)]
    [InlineData("-1", true, -1)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    public void TryParse_ReadsAnIntBackedIdentifier(string text, bool expected, int expectedValue)
    {
        TryParse<OrderId>(text, CultureInfo.InvariantCulture, out var parsed).Should().Be(expected);
        parsed.Value.Should().Be(expectedValue);
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        TryParse<OrderId>(null, CultureInfo.InvariantCulture, out var parsed).Should().BeFalse();
        parsed.Should().Be(default(OrderId));
    }

    [Fact]
    public void TryParse_ReadsEverySupportedPrimitive()
    {
        TryParse<LineId>("9007199254740993", CultureInfo.InvariantCulture, out var line).Should().BeTrue();
        line.Value.Should().Be(9007199254740993L);

        var guid = Guid.NewGuid();
        TryParse<SpeakerId>(guid.ToString(), CultureInfo.InvariantCulture, out var speaker).Should().BeTrue();
        speaker.Value.Should().Be(guid);

        TryParse<SkuId>("SKU-1", CultureInfo.InvariantCulture, out var sku).Should().BeTrue();
        sku.Value.Should().Be("SKU-1");
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionOnBadInput()
    {
        Parse<OrderId>("42", CultureInfo.InvariantCulture).Should().Be(OrderId.From(42));

        var act = () => Parse<OrderId>("abc", CultureInfo.InvariantCulture);

        act.Should().Throw<FormatException>().WithMessage("*OrderId*");
    }

    [Fact]
    public void Parse_UsesInvariantCultureWhenNoProviderIsGiven()
    {
        // A route segment is never culture-formatted, so a null provider must not fall back to the
        // request culture: the same "42" has to parse under every thread culture.
        StronglyTypedId.TryParse<OrderId, int>("42", provider: null, out var parsed).Should().BeTrue();
        parsed.Value.Should().Be(42);
    }

    [Fact]
    public void GetValueType_ReportsTheWrappedPrimitive()
    {
        StronglyTypedId.GetValueType(typeof(OrderId)).Should().Be<int>();
        StronglyTypedId.GetValueType(typeof(LineId)).Should().Be<long>();
        StronglyTypedId.GetValueType(typeof(SpeakerId)).Should().Be<Guid>();
        StronglyTypedId.GetValueType(typeof(SkuId)).Should().Be<string>();
        StronglyTypedId.GetValueType(typeof(int)).Should().BeNull();
        StronglyTypedId.GetValueType(typeof(string)).Should().BeNull();
    }

    [Fact]
    public void TryDescribe_UnwrapsANullableIdentifier()
    {
        StronglyTypedId.TryDescribe(typeof(OrderId?), out var identifierType, out var valueType).Should().BeTrue();
        identifierType.Should().Be<OrderId>();
        valueType.Should().Be<int>();

        StronglyTypedId.TryDescribe(typeof(int?), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void CanParseValues_CoversTheFourSupportedPrimitives()
    {
        StronglyTypedId.CanParseValues<int>().Should().BeTrue();
        StronglyTypedId.CanParseValues<long>().Should().BeTrue();
        StronglyTypedId.CanParseValues<Guid>().Should().BeTrue();
        StronglyTypedId.CanParseValues<string>().Should().BeTrue();
    }

    [Fact]
    public void Registry_DiscoversEveryIdentifierInTheAssembly()
    {
        var registry = new StronglyTypedIdRegistry(typeof(OrderId).Assembly);

        registry.IdentifierTypes.Should().Contain([
            typeof(CustomerId), typeof(LineId), typeof(OrderId), typeof(SkuId), typeof(SpeakerId)
        ]);
        registry.Describe().Should().Contain((typeof(OrderId), typeof(int)));
    }

    [Fact]
    public void Registry_RejectsATypeThatIsNotAnIdentifier()
    {
        var act = () => new StronglyTypedIdRegistry([typeof(int)]);

        act.Should().Throw<ArgumentException>().WithMessage("*IStronglyTypedId*");
    }

    /// <summary>
    /// Reaches the interface's default <see cref="IParsable{T}"/> implementation the way every
    /// framework boundary does. It cannot be called as <c>OrderId.TryParse(...)</c>, because a
    /// default implementation of an inherited static abstract member has to be explicit; that is a
    /// documented consequence of the two-line declaration, not a defect.
    /// </summary>
    private static bool TryParse<T>(string? text, IFormatProvider? provider, out T result)
        where T : IParsable<T>
        => T.TryParse(text, provider, out result!);

    /// <summary>The throwing leg of the same interface implementation.</summary>
    private static T Parse<T>(string text, IFormatProvider? provider)
        where T : IParsable<T>
        => T.Parse(text, provider);
}
