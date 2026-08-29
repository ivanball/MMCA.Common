using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class BaseEntityTests
{
    private sealed class TestEntity : BaseEntity<int>;

    private sealed class OtherTestEntity : BaseEntity<int>;

    private sealed class StringIdEntity : BaseEntity<string>;

    private sealed class GuidIdEntity : BaseEntity<Guid>;

    // ── Id property ──
    [Fact]
    public void Id_CanBeSetViaInitializer()
    {
        var entity = new TestEntity { Id = 42 };

        entity.Id.Should().Be(42);
    }

    [Fact]
    public void Id_WithStringType_CanBeSetViaInitializer()
    {
        var entity = new StringIdEntity { Id = "my-id" };

        entity.Id.Should().Be("my-id");
    }

    [Fact]
    public void Id_WithGuidType_CanBeSetViaInitializer()
    {
        var guid = Guid.NewGuid();
        var entity = new GuidIdEntity { Id = guid };

        entity.Id.Should().Be(guid);
    }

    // ── Implements IBaseEntity ──
    [Fact]
    public void Entity_ImplementsIBaseEntity() =>
        typeof(TestEntity).Should().Implement<IBaseEntity<int>>();

    // ── Abstract class ──
    [Fact]
    public void BaseEntity_IsAbstract() =>
        typeof(BaseEntity<int>).IsAbstract.Should().BeTrue();

    // ── Identity equality ──
    [Fact]
    public void Equals_SameTypeSameId_IsTrue()
    {
        var left = new TestEntity { Id = 42 };
        var right = new TestEntity { Id = 42 };

        left.Equals(right).Should().BeTrue();
        (left == right).Should().BeTrue();
        (left != right).Should().BeFalse();
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    [Fact]
    public void Equals_SameIdDifferentDerivedType_IsFalse()
    {
        // Identity is (concrete type, id): a ticket 7 is not a comment 7, even though both are
        // BaseEntity<int> instances carrying the same key.
        var left = new TestEntity { Id = 7 };
        var right = new OtherTestEntity { Id = 7 };

        left.Equals(right).Should().BeFalse();
        right.Equals(left).Should().BeFalse();
        left.GetHashCode().Should().NotBe(right.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentId_IsFalse()
    {
        var left = new TestEntity { Id = 1 };
        var right = new TestEntity { Id = 2 };

        left.Equals(right).Should().BeFalse();
        (left == right).Should().BeFalse();
        (left != right).Should().BeTrue();
    }

    [Fact]
    public void Equals_TwoTransientEntities_IsFalse()
    {
        // A default id means "not identified yet". Two unsaved entities with a database-generated
        // key would otherwise all collapse into one, which is the classic DDD equality bug.
        var left = new TestEntity { Id = 0 };
        var right = new TestEntity { Id = 0 };

        left.Equals(right).Should().BeFalse();
        (left == right).Should().BeFalse();
    }

    [Fact]
    public void Equals_TransientEntityComparedToItself_IsTrue()
    {
        var entity = new TestEntity { Id = 0 };
        var sameInstance = entity;

        entity.Equals(sameInstance).Should().BeTrue();
        (entity == sameInstance).Should().BeTrue();
        (entity != sameInstance).Should().BeFalse();
    }

    [Fact]
    public void Equals_NonEntity_IsFalse()
    {
        object other = "not an entity";

        new TestEntity { Id = 5 }.Equals(other).Should().BeFalse();
    }

    // ── Null handling through the operators ──
    [Fact]
    [SuppressMessage(
        "Maintainability",
        "CA1508:Avoid dead conditional code",
        Justification = "The point of the test is to pin the user-defined operators' null paths; the operands are deliberately known constants.")]
    public void Operators_WithNullOperands_CompareNullToNullOnly()
    {
        var entity = new TestEntity { Id = 3 };
        TestEntity? nothing = null;
        TestEntity? alsoNothing = null;

        (nothing == alsoNothing).Should().BeTrue();
        (nothing != alsoNothing).Should().BeFalse();
        (entity == nothing).Should().BeFalse();
        (nothing == entity).Should().BeFalse();
        (entity != nothing).Should().BeTrue();
        (nothing != entity).Should().BeTrue();
    }

    // ── Identifier types other than int ──
    [Fact]
    public void Equals_WithStringId_ComparesByValue()
    {
        var left = new StringIdEntity { Id = "my-id" };
        var right = new StringIdEntity { Id = "my-id" };

        (left == right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode());
        (left == new StringIdEntity { Id = "other-id" }).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithGuidId_ComparesByValue()
    {
        var id = Guid.NewGuid();
        var left = new GuidIdEntity { Id = id };
        var right = new GuidIdEntity { Id = id };

        (left == right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode());
        (left == new GuidIdEntity { Id = Guid.NewGuid() }).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithEmptyGuidId_TreatsBothSidesAsTransient()
    {
        // Guid.Empty is the default for a Guid key, so it means "not identified yet" exactly the
        // way 0 does for an int key.
        var left = new GuidIdEntity { Id = Guid.Empty };
        var right = new GuidIdEntity { Id = Guid.Empty };

        (left == right).Should().BeFalse();
    }
}
