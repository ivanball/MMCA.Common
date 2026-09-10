using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Shared.Tests.Identifiers;

/// <summary>
/// The wire contract: a wrapped identifier serializes as the bare primitive, so replacing an
/// <see langword="int"/> alias with a wrapper is invisible to every client. Mirrors
/// <c>EnumerationSerializationTests</c>, including the limitation that the factory has to be
/// registered on the options (there is no attribute on the wrapper to fall back to).
/// </summary>
public sealed class StronglyTypedIdSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new StronglyTypedIdJsonConverterFactory() }
    };

    private sealed record OrderDto(OrderId Id, CustomerId? CustomerId, SkuId Sku, decimal Total);

    [Fact]
    public void Serializes_AsTheBarePrimitive()
    {
        var json = JsonSerializer.Serialize(OrderId.From(42), Options);

        json.Should().Be("42");
    }

    [Fact]
    public void Deserializes_FromTheBarePrimitive()
    {
        JsonSerializer.Deserialize<OrderId>("42", Options).Should().Be(OrderId.From(42));
        JsonSerializer.Deserialize<SkuId>("\"SKU-1\"", Options).Should().Be(SkuId.From("SKU-1"));

        var guid = Guid.NewGuid();
        JsonSerializer.Deserialize<SpeakerId>($"\"{guid}\"", Options).Should().Be(SpeakerId.From(guid));
    }

    [Fact]
    public void RoundTrips_ANestedDto()
    {
        var original = new OrderDto(OrderId.From(7), CustomerId.From(3), SkuId.From("SKU-9"), 19.95m);

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<OrderDto>(json, Options);

        json.Should().Contain("\"Id\":7").And.Contain("\"CustomerId\":3").And.Contain("\"Sku\":\"SKU-9\"");
        json.Should().NotContain("Value", "the wrapper is not an object on the wire");
        restored.Should().Be(original);
    }

    [Fact]
    public void NullableIdentifier_RoundTripsAsNull()
    {
        var original = new OrderDto(OrderId.From(7), CustomerId: null, SkuId.From("SKU-9"), 1m);

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<OrderDto>(json, Options);

        json.Should().Contain("\"CustomerId\":null");
        restored!.CustomerId.Should().BeNull();
    }

    [Fact]
    public void Collections_SerializeAsPrimitiveArrays()
    {
        var json = JsonSerializer.Serialize(new[] { OrderId.From(1), OrderId.From(2) }, Options);

        json.Should().Be("[1,2]");
    }

    [Fact]
    public void DictionaryKey_UsesTheWrappedValue()
    {
        var json = JsonSerializer.Serialize(
            new Dictionary<OrderId, string> { [OrderId.From(5)] = "five" },
            Options);

        json.Should().Be("{\"5\":\"five\"}");
        JsonSerializer.Deserialize<Dictionary<OrderId, string>>(json, Options)!
            .Should().ContainKey(OrderId.From(5));
    }

    [Fact]
    public void Factory_ConvertsOnlyIdentifierTypes()
    {
        var factory = new StronglyTypedIdJsonConverterFactory();

        factory.CanConvert(typeof(OrderId)).Should().BeTrue();
        factory.CanConvert(typeof(int)).Should().BeFalse();
        factory.CanConvert(typeof(string)).Should().BeFalse();
        factory.CanConvert(typeof(OrderDto)).Should().BeFalse();
    }

    [Fact]
    public void WithoutTheFactory_TheWrapperFallsBackToTheObjectShape()
    {
        // The documented trap, the same one Enumeration<T> carries: there is no attribute on a
        // consumer's wrapper, so options that never registered the factory produce the default
        // struct shape. AddAPI registers it once for the whole API surface.
        var json = JsonSerializer.Serialize(OrderId.From(42), JsonSerializerOptions.Default);

        json.Should().Contain("Value");
    }
}
