using System.Dynamic;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// <c>SetConcurrencyETag</c> as it lives on <see cref="ApiControllerBase"/>: any controller, not only
/// an entity controller, can emit a served row's concurrency token as a weak <c>ETag</c>. The token is
/// read off the row's RUNTIME shape: a public <c>byte[] RowVersion</c> property on a typed value, or
/// the <c>rowVersion</c> (else <c>RowVersion</c>) entry of a field-shaped dictionary.
/// </summary>
public sealed class ApiControllerBaseETagTests
{
    /// <summary>The weak ETag <see cref="RowVersion"/> formats to.</summary>
    private const string ExpectedETag = "W/\"AAAAAAAAB9E=\"";

    private static readonly byte[] RowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

    private static readonly byte[] OtherRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];

    private static ConditionalCommandProbeController CreateController() =>
        new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().BuildServiceProvider(),
                },
            },
        };

    [Fact]
    public void Null_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(null);

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public void TypedDtoWithRowVersion_SetsWeakETag()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeVersioned { RowVersion = RowVersion });

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(ExpectedETag);
    }

    [Fact]
    public void TypedDtoWithoutRowVersion_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeUnversioned { Id = 1 });

        sut.Response.Headers.Should().NotContainKey(
            ConcurrencyETag.ETagHeaderName,
            "a value with no version has no precondition to offer");
    }

    [Fact]
    public void EmptyRowVersion_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeVersioned { RowVersion = [] });

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public void NullRowVersion_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeVersioned { RowVersion = null });

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public void ShapedDictionaryCamelCaseKey_SetsHeader()
    {
        var sut = CreateController();
        var shaped = new ExpandoObject();
        ((IDictionary<string, object?>)shaped)["id"] = 1;
        ((IDictionary<string, object?>)shaped)["rowVersion"] = RowVersion;

        sut.RunSetConcurrencyETag(shaped);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(ExpectedETag);
    }

    [Fact]
    public void ShapedDictionaryPascalCaseKey_SetsHeader()
    {
        var sut = CreateController();
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = 1,
            ["RowVersion"] = RowVersion,
        };

        sut.RunSetConcurrencyETag(shaped);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(ExpectedETag);
    }

    [Fact]
    public void ShapedDictionaryWithBothKeys_CamelCaseWins()
    {
        var sut = CreateController();
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["RowVersion"] = OtherRowVersion,
            ["rowVersion"] = RowVersion,
        };

        sut.RunSetConcurrencyETag(shaped);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(
            ExpectedETag,
            "the camelCase JSON name is read first and RowVersion only as the fallback");
    }

    [Fact]
    public void ShapedDictionaryWithoutToken_NoHeader()
    {
        var sut = CreateController();
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = 1 };

        sut.RunSetConcurrencyETag(shaped);

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public void ShapedDictionaryTokenThatIsNotBytes_NoHeader()
    {
        var sut = CreateController();
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal) { ["rowVersion"] = "AAAAAAAAB9E=" };

        sut.RunSetConcurrencyETag(shaped);

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public void RowVersionPropertyThatIsNotBytes_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeStringToken { RowVersion = "AAAAAAAAB9E=" });

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName, "only a byte[] RowVersion is a token");
    }

    [Fact]
    public void RowVersionNameWithOtherCasing_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeOtherCasing { Rowversion = RowVersion });

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName, "the property name must be exactly RowVersion");
    }

    [Fact]
    public void NonPublicRowVersion_NoHeader()
    {
        var sut = CreateController();

        sut.RunSetConcurrencyETag(new ETagProbeHiddenToken(RowVersion));

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName, "only a public instance property is read");
    }

    [Fact]
    public void RuntimeTypeIsInspected_NotTheStaticType()
    {
        var sut = CreateController();
        ETagProbeBase value = new ETagProbeDerivedVersioned { RowVersion = RowVersion };

        sut.RunSetConcurrencyETag(value);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(
            ExpectedETag,
            "the token is resolved from the value's runtime type");
    }

    [Fact]
    public void DifferentRuntimeTypes_EachResolvedByItsOwnShape()
    {
        var first = CreateController();
        var second = CreateController();
        var third = CreateController();

        first.RunSetConcurrencyETag(new ETagProbeVersioned { RowVersion = RowVersion });
        second.RunSetConcurrencyETag(new ETagProbeUnversioned { Id = 1 });
        third.RunSetConcurrencyETag(new ETagProbeVersioned { RowVersion = OtherRowVersion });

        first.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(ExpectedETag);
        second.Response.Headers.Should().NotContainKey(
            ConcurrencyETag.ETagHeaderName,
            "a type resolved after another one must not reuse the first type's property");
        third.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(
            ConcurrencyETag.Format(OtherRowVersion),
            "the per-type resolution reads each value's own token");
    }
}

/// <summary>A typed row with a public byte[] RowVersion.</summary>
internal sealed class ETagProbeVersioned
{
    public byte[]? RowVersion { get; init; }
}

/// <summary>A typed row with no token.</summary>
internal sealed class ETagProbeUnversioned
{
    public int Id { get; init; }
}

/// <summary>A row whose RowVersion is not a byte array.</summary>
internal sealed class ETagProbeStringToken
{
    public string? RowVersion { get; init; }
}

/// <summary>A row whose token property differs from RowVersion only by casing.</summary>
internal sealed class ETagProbeOtherCasing
{
    public byte[]? Rowversion { get; init; }
}

/// <summary>A row whose RowVersion is not public.</summary>
internal sealed class ETagProbeHiddenToken(byte[] rowVersion)
{
    internal byte[] RowVersion { get; } = rowVersion;
}

/// <summary>A base row type with no token of its own.</summary>
internal class ETagProbeBase
{
    public int Id { get; init; }
}

/// <summary>A derived row type that adds the token.</summary>
internal sealed class ETagProbeDerivedVersioned : ETagProbeBase
{
    public byte[]? RowVersion { get; init; }
}
