using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;

namespace MMCA.Common.Application.Tests.Services.Filtering;

/// <summary>
/// The filter property cache is static and lives for the life of the process, and the names
/// probed against it arrive in the query string. Caching MISSES therefore handed any caller an
/// unbounded, never-evicted dictionary they could grow at will, one entry per bogus filter name,
/// while the request itself came back as a tidy 400 that showed nothing in error metrics.
/// </summary>
public class QueryFilterServicePropertyCacheTests
{
    private static readonly Dictionary<string, string> EmptyMap = [];

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Counts the cache entries belonging to <see cref="Widget"/> only. Asserting on the real static
    /// field is the point (any indirect proxy would keep passing if negative caching came back), but
    /// the whole assembly's filter tests share that field and run in parallel, so a total count would
    /// race them. <see cref="Widget"/> is private to this class, so its slice is ours alone.
    /// </summary>
    private static int CacheEntryCount()
    {
        var field = typeof(QueryFilterService).GetField("PropertyCache", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("the test pins the real cache, not a stand-in");

        var cache = (IEnumerable<KeyValuePair<(Type EntityType, string PropertyName), PropertyInfo>>)field!.GetValue(null)!;
        return cache.Count(entry => entry.Key.EntityType == typeof(Widget));
    }

    [Fact]
    public void ValidateFilters_WithManyUnknownProperties_DoesNotGrowTheStaticCache()
    {
        // Warm any entry the type legitimately caches, so the delta below measures only the misses.
        QueryFilterService.ValidateFilters<Widget>(
            new Dictionary<string, (string, string)> { ["Name"] = ("EQUALS", "x") },
            EmptyMap);

        var before = CacheEntryCount();

        for (var i = 0; i < 500; i++)
        {
            var bogus = string.Create(CultureInfo.InvariantCulture, $"NotAProperty{i}");
            var result = QueryFilterService.ValidateFilters<Widget>(
                new Dictionary<string, (string, string)> { [bogus] = ("EQUALS", "x") },
                EmptyMap);

            result.IsFailure.Should().BeTrue("an unknown filter property must still fail closed");
        }

        CacheEntryCount().Should().Be(
            before,
            "unresolvable names come from the client, so memoizing the miss is an unbounded static leak");
    }

    [Fact]
    public void ValidateFilters_WithKnownProperty_StillResolvesAndSucceeds()
    {
        // The fix must not cost correctness on the path that matters: hits are still cached and
        // repeated resolution keeps working.
        for (var i = 0; i < 3; i++)
        {
            var result = QueryFilterService.ValidateFilters<Widget>(
                new Dictionary<string, (string, string)> { ["Name"] = ("CONTAINS", "abc") },
                EmptyMap);

            result.IsSuccess.Should().BeTrue();
        }

        CacheEntryCount().Should().BeGreaterThan(0, "resolved lookups are still memoized");
    }

    [Fact]
    public void ApplyFilters_WithUnknownProperty_LeavesTheQueryUnfilteredWithoutCaching()
    {
        var widgets = new[] { new Widget { Id = 1, Name = "a" }, new Widget { Id = 2, Name = "b" } }.AsQueryable();
        var before = CacheEntryCount();

        var filtered = QueryFilterService.ApplyFilters(
            widgets,
            new Dictionary<string, (string Operator, string Value)> { ["Nope"] = ("EQUALS", "a") },
            EmptyMap);

        filtered.Count().Should().Be(2);
        CacheEntryCount().Should().Be(before);
    }
}
