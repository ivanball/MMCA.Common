using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// The seek predicate's comparison has three shapes: <see cref="string.Compare(string, string)"/> for
/// strings, the relational operator for types that have one (a <see cref="Guid"/> key has had one since
/// .NET 8), and <c>CompareTo</c> for a type that only implements <see cref="IComparable{T}"/>
/// (<see cref="RankKey"/> here). A type with neither is refused
/// with a message naming it. The predicates are compiled and run in memory over non-null keys, so the
/// assertions are about which rows follow the boundary, not about the expression shape.
/// </summary>
public sealed class KeysetQueryBuilderComparisonTests
{
    private static readonly Guid First = new(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Guid Second = new(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Guid Third = new(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static readonly ComparisonEntity[] Rows =
    [
        new() { Id = First, Name = "alpha", Token = Third, Score = 30m, Stamp = new DateOnly(2026, 1, 3), Version = new Version(1, 0), Rank = new RankKey(5) },
        new() { Id = Second, Name = "bravo", Token = First, Score = 10m, Stamp = new DateOnly(2026, 1, 1), Version = new Version(3, 0), Rank = new RankKey(1) },
        new() { Id = Third, Name = "charlie", Token = Second, Score = 20m, Stamp = new DateOnly(2026, 1, 2), Version = new Version(2, 0), Rank = new RankKey(3) },
    ];

    [Fact]
    public void ComparableOnlySortKey_Ascending_ComparesThroughCompareTo()
    {
        // Boundary is Rank 3 (Id Third): only Rank 5 (Id First) follows it.
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Rank)), new RankKey(3), Third, descending: false);

        Select(seek).Should().Equal(First);
    }

    [Fact]
    public void ComparableOnlySortKey_Descending_ComparesThroughCompareTo()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Rank)), new RankKey(3), Third, descending: true);

        Select(seek).Should().Equal(Second);
    }

    [Fact]
    public void GuidIdOnly_Ascending_SelectsTheLaterIdentifiers()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(null, null, First, descending: false);

        Select(seek).Should().Equal(Second, Third);
    }

    [Fact]
    public void GuidIdOnly_Descending_SelectsTheEarlierIdentifiers()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(null, null, Third, descending: true);

        Select(seek).Should().Equal(First, Second);
    }

    [Fact]
    public void GuidSortKey_Ascending_SelectsTheLaterTokens()
    {
        // Boundary is the row whose Token is Second (Id Third): later tokens are only Third (Id First).
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Token)), Second, Third, descending: false);

        Select(seek).Should().Equal(First);
    }

    [Fact]
    public void GuidSortKey_Descending_SelectsTheEarlierTokens()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Token)), Second, Third, descending: true);

        Select(seek).Should().Equal(Second);
    }

    [Fact]
    public void StringSortKey_Ascending_SelectsTheLaterNames()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Name)), "bravo", Second, descending: false);

        Select(seek).Should().Equal(Third);
    }

    [Fact]
    public void StringSortKey_Descending_SelectsTheEarlierNames()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Name)), "bravo", Second, descending: true);

        Select(seek).Should().Equal(First);
    }

    [Fact]
    public void DecimalSortKey_UsesTheRelationalOperator_InBothDirections()
    {
        var property = Property(nameof(ComparisonEntity.Score));

        Select(KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(property, 20m, Third, descending: false))
            .Should().Equal(First);
        Select(KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(property, 20m, Third, descending: true))
            .Should().Equal(Second);
    }

    [Fact]
    public void DateOnlySortKey_UsesTheRelationalOperator()
    {
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Stamp)), new DateOnly(2026, 1, 2), Third, descending: false);

        Select(seek).Should().Equal(First);
    }

    [Fact]
    public void TypeWithAGreaterThanOperator_UsesThatOperator()
    {
        // Version is not in the built-in list, but declares op_GreaterThan.
        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Version)), new Version(2, 0), Third, descending: false);

        Select(seek).Should().Equal(Second);
    }

    [Fact]
    public void EqualSortKeys_FallBackToTheAscendingIdTieBreak()
    {
        ComparisonEntity[] tied =
        [
            new() { Id = First, Name = "same" },
            new() { Id = Second, Name = "same" },
            new() { Id = Third, Name = "same" },
        ];

        var seek = KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Name)), "same", Second, descending: true);

        tied.AsQueryable().Where(seek).Select(r => r.Id).Should().Equal(
            [Third],
            "the identifier half always ascends, even on a descending page");
    }

    [Fact]
    public void TypeWithNeitherAnOperatorNorIComparable_IsRefusedByName()
    {
        var act = () => KeysetQueryBuilder.BuildSeekPredicate<ComparisonEntity, Guid>(
            Property(nameof(ComparisonEntity.Opaque)), new OpaqueKey(1), First, descending: false);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*'OpaqueKey'*neither a relational operator nor IComparable<T>*");
    }

    private static Guid[] Select(System.Linq.Expressions.Expression<Func<ComparisonEntity, bool>> seek) =>
        [.. Rows.AsQueryable().Where(seek).OrderBy(r => r.Id).Select(r => r.Id)];

    private static PropertyInfo Property(string name) => typeof(ComparisonEntity).GetProperty(name)!;

    /// <summary>A key type with no relational operator and no <see cref="IComparable{T}"/>.</summary>
    /// <param name="Value">The carried value.</param>
    public readonly record struct OpaqueKey(int Value);

    /// <summary>An entity carrying one sort key of each comparison shape.</summary>
    internal sealed class ComparisonEntity : AuditableBaseEntity<Guid>
    {
        public string Name { get; set; } = string.Empty;

        public Guid Token { get; set; }

        public decimal Score { get; set; }

        public DateOnly Stamp { get; set; }

        public Version Version { get; set; } = new(0, 0);

        public OpaqueKey Opaque { get; set; }

        public RankKey Rank { get; set; }
    }

    /// <summary>A key type that orders through <see cref="IComparable{T}"/> alone, with no relational operator.</summary>
    /// <param name="Value">The carried value.</param>
    internal readonly record struct RankKey(int Value) : IComparable<RankKey>
    {
        /// <inheritdoc />
        public int CompareTo(RankKey other) => Value.CompareTo(other.Value);
    }
}
