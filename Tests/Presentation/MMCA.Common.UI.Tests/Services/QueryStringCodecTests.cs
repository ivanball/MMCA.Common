using AwesomeAssertions;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the query-string reading and writing the list pages rely on, case by case: the leading
/// question mark, '+' as a space, percent-decoding (and malformed escapes left as typed), repeated
/// and case-variant keys, empty values and empty segments on the way in; encoding, a null value
/// skipped, an existing query extended and a fragment kept last on the way out.
/// </summary>
public sealed class QueryStringCodecTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("&&")]
    [InlineData("?&")]
    public void Parse_OfNothing_IsEmpty(string? query) => QueryStringCodec.Parse(query).Should().BeEmpty();

    [Fact]
    public void Parse_SkipsOneLeadingQuestionMark_Only()
    {
        QueryStringCodec.Parse("?p=1")["p"].ToString().Should().Be("1");
        QueryStringCodec.Parse("p=1")["p"].ToString().Should().Be("1");
        QueryStringCodec.Parse("??p=1").Should().ContainKey("?p");
    }

    [Theory]
    [InlineData("q=a+b", "a b")]
    [InlineData("q=a%20b", "a b")]
    [InlineData("q=a%2Bb", "a+b")]
    [InlineData("q=a%2bb", "a+b")]
    [InlineData("q=caf%C3%A9", "caf\u00E9")]
    [InlineData("q=100%", "100%")]
    [InlineData("q=%zz", "%zz")]
    [InlineData("q=a=b", "a=b")]
    [InlineData("q=a;b", "a;b")]
    [InlineData("q=a#b", "a#b")]
    public void Parse_DecodesValues(string query, string expected) =>
        QueryStringCodec.Parse(query)["q"].ToString().Should().Be(expected);

    [Fact]
    public void Parse_DecodesKeys()
    {
        var parsed = QueryStringCodec.Parse("f%3AName=x&c:Unit+Price=eq:5");

        parsed["f:Name"].ToString().Should().Be("x");
        parsed["c:Unit Price"].ToString().Should().Be("eq:5");
    }

    [Fact]
    public void Parse_AKeyWithoutAValue_OrWithAnEmptyOne_HasTheEmptyValue()
    {
        var parsed = QueryStringCodec.Parse("d&s=");

        parsed["d"].ToString().Should().BeEmpty();
        parsed["s"].ToString().Should().BeEmpty();
        parsed["d"].Should().HaveCount(1);
    }

    [Fact]
    public void Parse_RepeatedAndCaseVariantKeys_AccumulateInOrder_UnderTheFirstSpelling()
    {
        var parsed = QueryStringCodec.Parse("P=1&tag=a&p=2&TAG=b&tag=");

        parsed.Keys.Should().Equal("P", "tag");
        parsed["p"].ToArray().Should().Equal("1", "2");
        parsed["Tag"].ToArray().Should().Equal("a", "b", string.Empty);
        parsed["tag"].ToString().Should().Be("a,b", "StringValues joins the non-empty values, which is what the list page reads");
    }

    [Fact]
    public void AddQueryString_EncodesKeysAndValues()
    {
        var path = QueryStringCodec.AddQueryString(
            "/items",
            [new("q", "a b+c&d=e"), new("f:Name", "caf\u00E9"), new("c:Price", "eq:5")]);

        path.Should().Be("/items?q=a%20b%2Bc%26d%3De&f%3AName=caf%C3%A9&c%3APrice=eq%3A5");
    }

    [Fact]
    public void AddQueryString_SkipsANullValue_ButWritesAnEmptyOne()
    {
        var path = QueryStringCodec.AddQueryString("/items", [new("a", null), new("b", string.Empty), new("c", "1")]);

        path.Should().Be("/items?b=&c=1");
    }

    [Theory]
    [InlineData("/items?x=1", "/items?x=1&p=2")]
    [InlineData("/items?", "/items?&p=2")]
    [InlineData("/items#top", "/items?p=2#top")]
    [InlineData("/items?x=1#top", "/items?x=1&p=2#top")]
    [InlineData("/items#a?b", "/items?p=2#a?b")]
    public void AddQueryString_ExtendsAnExistingQuery_AndKeepsTheFragmentLast(string basePath, string expected) =>
        QueryStringCodec.AddQueryString(basePath, [new("p", "2")]).Should().Be(expected);

    [Fact]
    public void AddQueryString_ThenParse_RoundTrips()
    {
        var path = QueryStringCodec.AddQueryString("/items", [new("q", "50% off + more"), new("f:Status", "a&b=c")]);

        var parsed = QueryStringCodec.Parse(path[path.IndexOf('?', StringComparison.Ordinal)..]);

        parsed["q"].ToString().Should().Be("50% off + more");
        parsed["f:Status"].ToString().Should().Be("a&b=c");
    }
}
