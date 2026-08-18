using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

/// <summary>
/// Covers the keyset paging value types: the request's clamp semantics, the page result, and the
/// cursor codec (round-trip, version gate, and rejection of anything malformed).
/// </summary>
public sealed class KeysetPaginationTests
{
    // ── KeysetPageRequest ──
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    public void PageSize_IsClampedIntoTheAllowedRange(int requested, int expected) =>
        new KeysetPageRequest(requested).PageSize.Should().Be(expected);

    [Fact]
    public void PageSize_IsAlsoClampedThroughTheInitializer()
    {
        var request = new KeysetPageRequest(10) { PageSize = 999_999 };

        request.PageSize.Should().Be(KeysetPageRequest.MaxPageSize);
    }

    [Fact]
    public void ParameterlessConstructor_ProducesAMinimalFirstPageRequest()
    {
        var request = new KeysetPageRequest();

        request.PageSize.Should().Be(1);
        request.SortColumn.Should().BeNull();
        request.Descending.Should().BeFalse();
        request.Cursor.Should().BeNull();
    }

    [Fact]
    public void Constructor_KeepsTheSortAndCursorItWasGiven()
    {
        var request = new KeysetPageRequest(25, "CreatedOn", descending: true, cursor: "abc");

        request.SortColumn.Should().Be("CreatedOn");
        request.Descending.Should().BeTrue();
        request.Cursor.Should().Be("abc");
    }

    // ── KeysetCollectionResult ──
    [Fact]
    public void KeysetCollectionResult_CarriesItemsAndCursor()
    {
        var result = new KeysetCollectionResult<int>([1, 2, 3], "next");

        result.Items.Should().Equal(1, 2, 3);
        result.NextCursor.Should().Be("next");
    }

    [Fact]
    public void KeysetCollectionResult_Empty_HasNoCursor()
    {
        var result = new KeysetCollectionResult<int>();

        result.Items.Should().BeEmpty();
        result.NextCursor.Should().BeNull();
    }

    [Fact]
    public void KeysetCollectionResult_IsACollectionResult() =>
        new KeysetCollectionResult<int>([1], null).Should().BeAssignableTo<CollectionResult<int>>();

    [Fact]
    public void KeysetCollectionResult_WithNullItems_Throws()
    {
        var act = () => new KeysetCollectionResult<int>(null!, null);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── KeysetCursor round-trip ──
    [Theory]
    [InlineData("Widget", "42")]
    [InlineData("", "1")]
    [InlineData("a|b|c", "7")]
    [InlineData("v1|0||x", "8")]
    [InlineData("Ünïcödé ✓", "9")]
    [InlineData("2026-08-17T12:34:56.7890123Z", "10")]
    public void Encode_ThenDecode_ReturnsTheSameValues(string sortValue, string id)
    {
        var cursor = KeysetCursor.Encode(sortValue, id);

        KeysetCursor.TryDecode(cursor, out var decodedSort, out var decodedId).Should().BeTrue();
        decodedSort.Should().Be(sortValue);
        decodedId.Should().Be(id);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesANullSortValue()
    {
        var cursor = KeysetCursor.Encode(null, "42");

        KeysetCursor.TryDecode(cursor, out var decodedSort, out var decodedId).Should().BeTrue();
        decodedSort.Should().BeNull("a null sort value must not decode as an empty string");
        decodedId.Should().Be("42");
    }

    [Fact]
    public void Encode_ProducesAnOpaqueUrlSafeToken()
    {
        var cursor = KeysetCursor.Encode("Widget", "42");

        cursor.Should().NotContain("|").And.NotContain("+").And.NotContain("/").And.NotContain("=");
        cursor.Should().NotContain("Widget", "the cursor is opaque, not a readable payload");
    }

    [Fact]
    public void Encode_WithNullId_Throws()
    {
        var act = () => KeysetCursor.Encode("Widget", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── KeysetCursor rejection ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cursor!!")]
    [InlineData("////")]
    public void TryDecode_RejectsMalformedInput(string? cursor)
    {
        KeysetCursor.TryDecode(cursor, out var sortValue, out var id).Should().BeFalse();
        sortValue.Should().BeNull();
        id.Should().BeEmpty();
    }

    [Fact]
    public void TryDecode_RejectsAnUnknownFormatVersion()
    {
        var forged = Encode("v2|1|V2lkZ2V0|NDI");

        KeysetCursor.TryDecode(forged, out _, out _).Should().BeFalse(
            "the version prefix exists so a future encoding cannot be mis-read as this one");
    }

    [Fact]
    public void TryDecode_RejectsAWrongSegmentCount()
    {
        KeysetCursor.TryDecode(Encode("v1|1|V2lkZ2V0"), out _, out _).Should().BeFalse();
        KeysetCursor.TryDecode(Encode("v1|1|V2lkZ2V0|NDI|extra"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_RejectsAnUnknownNullFlag() =>
        KeysetCursor.TryDecode(Encode("v1|2|V2lkZ2V0|NDI"), out _, out _).Should().BeFalse();

    [Fact]
    public void TryDecode_RejectsAnUndecodableSegment() =>
        KeysetCursor.TryDecode(Encode("v1|1|!!!!|NDI"), out _, out _).Should().BeFalse();

    private static string Encode(string payload) =>
        System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload));
}
