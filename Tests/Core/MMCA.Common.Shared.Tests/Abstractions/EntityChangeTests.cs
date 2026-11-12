using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

/// <summary>
/// The change-feed entry's wire shape: camelCase members, and a tombstone that still carries an
/// explicit <c>item: null</c> so a client tells "deleted" from "field missing".
/// </summary>
public sealed class EntityChangeTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serializes_CamelCase_TombstoneItemNull()
    {
        var changedOn = new DateTime(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc);
        var page = new KeysetCollectionResult<EntityChange<SampleDTO, int>>(
            [
                new EntityChange<SampleDTO, int> { Id = 1, IsDeleted = false, ChangedOn = changedOn, Item = new SampleDTO(1, "one") },
                new EntityChange<SampleDTO, int> { Id = 2, IsDeleted = true, ChangedOn = changedOn, Item = null },
            ],
            nextCursor: null);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, WebOptions));
        JsonElement root = json.RootElement;

        root.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement[] items = [.. root.GetProperty("items").EnumerateArray()];
        items.Should().HaveCount(2);

        items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "isDeleted", "changedOn", "item");
        items[0].GetProperty("id").GetInt32().Should().Be(1);
        items[0].GetProperty("isDeleted").GetBoolean().Should().BeFalse();
        items[0].GetProperty("changedOn").GetDateTime().Should().Be(changedOn);
        items[0].GetProperty("item").GetProperty("name").GetString().Should().Be("one", "an upsert carries the normal DTO");

        items[1].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "isDeleted", "changedOn", "item");
        items[1].GetProperty("isDeleted").GetBoolean().Should().BeTrue();
        items[1].GetProperty("item").ValueKind.Should().Be(JsonValueKind.Null, "a tombstone carries no row data");
    }

    [Fact]
    public void HasValueSemantics()
    {
        var changedOn = new DateTime(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc);

        var first = new EntityChange<SampleDTO, int> { Id = 2, IsDeleted = true, ChangedOn = changedOn };
        var second = new EntityChange<SampleDTO, int> { Id = 2, IsDeleted = true, ChangedOn = changedOn };

        first.Should().Be(second, "a client applies a change idempotently, so two equal entries are one change");
        first.Item.Should().BeNull("Item is optional and defaults to null");
    }

    /// <summary>A DTO standing in for a consumer's entity DTO.</summary>
    /// <param name="Id">The identifier.</param>
    /// <param name="Name">The display name.</param>
    public sealed record SampleDTO(int Id, string Name);
}
