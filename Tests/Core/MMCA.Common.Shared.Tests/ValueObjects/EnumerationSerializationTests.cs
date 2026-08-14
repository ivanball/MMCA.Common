using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

/// <summary>
/// Pins the JSON wire contract of the smart enumeration: the member name is the payload, and a
/// payload that cannot be resolved fails loudly instead of materializing a null member. Both routes
/// to the converter are covered, the attribute on the concrete type and the factory registered in
/// <see cref="JsonSerializerOptions.Converters"/>, because System.Text.Json resolves a converter
/// attribute off the type it is converting without walking base types.
/// </summary>
public class EnumerationSerializationTests
{
    // ── Round-trip ──
    [Fact]
    public void Serialize_WritesTheMemberName()
    {
        var json = JsonSerializer.Serialize(Severity.Major);

        json.Should().Be("\"Major\"", "the factory attached to the base class serializes by Name");
    }

    [Fact]
    public void Roundtrip_TopLevelMember_ReturnsTheDeclaredInstance()
    {
        var json = JsonSerializer.Serialize(Severity.Minor);

        var deserialized = JsonSerializer.Deserialize<Severity>(json);

        deserialized.Should().BeSameAs(Severity.Minor);
    }

    [Fact]
    public void Roundtrip_MemberOnAContainer_PreservesTheMember()
    {
        var json = JsonSerializer.Serialize(new Alert { Title = "Disk full", Level = Severity.Major });

        var deserialized = JsonSerializer.Deserialize<Alert>(json);

        json.Should().Contain("\"Level\":\"Major\"");
        deserialized!.Level.Should().BeSameAs(Severity.Major);
    }

    [Fact]
    public void Deserialize_NameWithDifferentCasing_ResolvesTheMember()
    {
        var deserialized = JsonSerializer.Deserialize<Severity>("\"mAJOR\"");

        deserialized.Should().BeSameAs(Severity.Major, "the converter resolves through the case-insensitive FromName");
    }

    // ── Factory registered in the options instead of attributed on the type ──
    [Fact]
    public void Roundtrip_ThroughAFactoryRegisteredInTheOptions_NeedsNoTypeAttribute()
    {
        var options = new JsonSerializerOptions { Converters = { new EnumerationJsonConverterFactory() } };

        var json = JsonSerializer.Serialize(Grade.Pass, options);
        var deserialized = JsonSerializer.Deserialize<Grade>(json, options);

        json.Should().Be("\"Pass\"");
        deserialized.Should().BeSameAs(Grade.Pass);
    }

    [Fact]
    public void Serialize_WithoutEitherRoute_FallsBackToTheDefaultObjectShape()
    {
        var json = JsonSerializer.Serialize(Grade.Fail);

        json.Should().Contain("\"Name\":\"Fail\"",
            "the documented limitation is real: an unattributed enumeration serialized through unconfigured options uses the default converter");
    }

    // ── Rejected payloads ──
    [Fact]
    public void Deserialize_UnknownName_ThrowsJsonException()
        => FluentActions.Invoking(() => JsonSerializer.Deserialize<Severity>("\"Catastrophic\""))
            .Should().Throw<JsonException>();

    [Fact]
    public void Deserialize_NumericToken_ThrowsJsonException()
        => FluentActions.Invoking(() => JsonSerializer.Deserialize<Severity>("2"))
            .Should().Throw<JsonException>(
                "a non-string token must fail the same way MVC model binding does, not bind by value");

    [Fact]
    public void Deserialize_ObjectToken_ThrowsJsonException()
        => FluentActions.Invoking(() => JsonSerializer.Deserialize<Severity>("{\"Name\":\"Major\"}"))
            .Should().Throw<JsonException>();

    // ── Null handling ──
    [Fact]
    public void Deserialize_NullToken_ReturnsNull()
        => JsonSerializer.Deserialize<Severity>("null").Should().BeNull(
            "HandleNull stays at its default, so the serializer short-circuits null before the converter runs");

    [Fact]
    public void Roundtrip_ContainerWithNullMember_KeepsTheMemberNull()
    {
        var json = JsonSerializer.Serialize(new Alert { Title = "Nothing to see" });

        var deserialized = JsonSerializer.Deserialize<Alert>(json);

        deserialized!.Level.Should().BeNull();
    }

    [JsonConverter(typeof(EnumerationJsonConverterFactory))]
    private sealed class Severity : Enumeration<Severity>
    {
        public static readonly Severity Minor = new(1, "Minor");
        public static readonly Severity Major = new(2, "Major");

        private Severity(int value, string name)
            : base(value, name)
        {
        }
    }

    private sealed class Grade : Enumeration<Grade>
    {
        public static readonly Grade Fail = new(1, "Fail");
        public static readonly Grade Pass = new(2, "Pass");

        private Grade(int value, string name)
            : base(value, name)
        {
        }
    }

    private sealed record Alert
    {
        public string Title { get; init; } = string.Empty;

        public Severity? Level { get; init; }
    }
}
