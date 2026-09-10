using System.ComponentModel;
using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Shared.Tests.Identifiers;

/// <summary>
/// The MVC binding path. A controller action taking <c>OrderId id</c> from a route segment binds
/// through <see cref="TypeDescriptor"/>, not through <see cref="IParsable{TSelf}"/>, so the
/// converter registration is what makes <c>GET /orders/42</c> work at all.
/// </summary>
public sealed class StronglyTypedIdTypeConverterTests
{
    [Fact]
    public void Converter_ReadsTextAndThePrimitive()
    {
        var converter = new StronglyTypedIdTypeConverter<OrderId, int>();

        converter.CanConvertFrom(context: null, typeof(string)).Should().BeTrue();
        converter.CanConvertFrom(context: null, typeof(int)).Should().BeTrue();
        converter.ConvertFrom(context: null, CultureInfo.InvariantCulture, "42").Should().Be(OrderId.From(42));
        converter.ConvertFrom(context: null, CultureInfo.InvariantCulture, 42).Should().Be(OrderId.From(42));
    }

    [Fact]
    public void Converter_WritesTextAndThePrimitive()
    {
        var converter = new StronglyTypedIdTypeConverter<OrderId, int>();

        converter.ConvertTo(context: null, CultureInfo.InvariantCulture, OrderId.From(42), typeof(int))
            .Should().Be(42);
        converter.ConvertTo(context: null, CultureInfo.InvariantCulture, OrderId.From(42), typeof(string))
            .Should().Be("42");
    }

    [Fact]
    public void Converter_RejectsTextThatIsNotTheWrappedPrimitive()
    {
        var converter = new StronglyTypedIdTypeConverter<OrderId, int>();

        var act = () => converter.ConvertFrom(context: null, CultureInfo.InvariantCulture, "abc");

        act.Should().Throw<FormatException>().WithMessage("*OrderId*");
    }

    [Fact]
    public void Registration_MakesTypeDescriptorResolveTheConverter()
    {
        // TypeDescriptor registration is process-global and idempotent, so this asserts the end
        // state rather than the transition: after registering, the type resolves to the framework's
        // converter rather than the default struct converter, which cannot read a string at all.
        StronglyTypedIdTypeConverters.Register(typeof(LineId));

        var converter = TypeDescriptor.GetConverter(typeof(LineId));
        converter.Should().BeOfType<StronglyTypedIdTypeConverter<LineId, long>>();
        converter.ConvertFrom(context: null, CultureInfo.InvariantCulture, "9").Should().Be(LineId.From(9));
    }

    [Fact]
    public void Register_RejectsATypeThatIsNotAnIdentifier()
    {
        var act = () => StronglyTypedIdTypeConverters.Register(typeof(int));

        act.Should().Throw<ArgumentException>().WithMessage("*IStronglyTypedId*");
    }

    [Fact]
    public void RegisterAll_CoversEveryIdentifierTheAssemblyDeclares()
    {
        var registered = StronglyTypedIdTypeConverters.RegisterAll(typeof(OrderId).Assembly);

        registered.Should().BeGreaterThanOrEqualTo(5);
        TypeDescriptor.GetConverter(typeof(SkuId))
            .ConvertFrom(context: null, CultureInfo.InvariantCulture, "SKU-4")
            .Should().Be(SkuId.From("SKU-4"));
    }
}
