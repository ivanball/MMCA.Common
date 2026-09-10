using AwesomeAssertions;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Application.Tests.Services.Filtering;

/// <summary>
/// A column typed as a strongly typed identifier filters by the primitive the client still sends
/// (ADR-115), and the strategy is resolved with no registration call. Only the equality family is
/// supported, and an ordering operator is rejected as a 400 rather than silently returning the
/// unfiltered set.
/// </summary>
public sealed class StronglyTypedIdFilterStrategyTests
{
    public readonly record struct OrderId(int Value) : IStronglyTypedId<OrderId, int>
    {
        /// <summary>Wraps a primitive order key.</summary>
        public static OrderId From(int value) => new(value);
    }

    public readonly record struct SkuId(string Value) : IStronglyTypedId<SkuId, string>
    {
        /// <summary>Wraps a stock-keeping unit.</summary>
        public static SkuId From(string value) => new(value);
    }

    private sealed class Item
    {
        public OrderId Id { get; set; }

        public OrderId? ParentId { get; set; }

        public SkuId Sku { get; set; }
    }

    private static readonly Dictionary<string, string> EmptyMap = [];

    private static IQueryable<Item> Items() =>
        new List<Item>
        {
            new() { Id = OrderId.From(1), ParentId = null, Sku = SkuId.From("A") },
            new() { Id = OrderId.From(2), ParentId = OrderId.From(1), Sku = SkuId.From("B") },
            new() { Id = OrderId.From(3), ParentId = OrderId.From(1), Sku = SkuId.From("C") },
        }.AsQueryable();

    private static IQueryable<Item> Filter(string property, string op, string value) =>
        QueryFilterService.ApplyFilters(
            Items(),
            new Dictionary<string, (string, string)>(StringComparer.Ordinal) { [property] = (op, value) },
            EmptyMap);

    [Fact]
    public void Equals_MatchesOnThePrimitiveTheClientSends() =>
        Filter("Id", "equals", "2").Should().ContainSingle().Which.Id.Should().Be(OrderId.From(2));

    [Fact]
    public void NotEquals_ExcludesTheMatch() =>
        Filter("Id", "not equals", "2").Should().HaveCount(2);

    [Fact]
    public void In_MatchesTheListedPrimitives() =>
        Filter("Id", "in", "1,3").Should().HaveCount(2);

    [Fact]
    public void IsEmpty_FindsTheUnsetOptionalIdentifier() =>
        Filter("ParentId", "is empty", "ignored").Should().ContainSingle();

    [Fact]
    public void IsNotEmpty_FindsTheSetOptionalIdentifiers() =>
        Filter("ParentId", "is not empty", "ignored").Should().HaveCount(2);

    [Fact]
    public void StringBackedIdentifier_FiltersByItsText() =>
        Filter("Sku", "equals", "B").Should().ContainSingle().Which.Sku.Should().Be(SkuId.From("B"));

    [Fact]
    public void Validate_AcceptsTheEqualityFamily() =>
        QueryFilterService.ValidateFilters<Item>(
            new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["Id"] = ("equals", "2") },
            EmptyMap)
            .IsSuccess.Should().BeTrue();

    [Fact]
    public void Validate_RejectsAnOrderingOperator()
    {
        // A record struct declares == and != and nothing else, so there is no > to build a range
        // predicate from. Refusing it is what stops a malformed range from widening the response.
        var result = QueryFilterService.ValidateFilters<Item>(
            new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["Id"] = ("greater than", "2") },
            EmptyMap);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Filter.Operator.NotSupported");
    }

    [Fact]
    public void Validate_RejectsAValueThatIsNotTheWrappedPrimitive()
    {
        var result = QueryFilterService.ValidateFilters<Item>(
            new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["Id"] = ("equals", "abc") },
            EmptyMap);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    // The point of the fallback in QueryFilterService.ResolveStrategy: a consumer that adopts
    // wrappers does not call RegisterStrategy once per identifier it declares.
    public void Strategy_IsResolvedWithoutARegisterStrategyCall() =>
        QueryFilterService.ValidateFilters<Item>(
            new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["Sku"] = ("in", "A,B") },
            EmptyMap)
            .IsSuccess.Should().BeTrue();
}
