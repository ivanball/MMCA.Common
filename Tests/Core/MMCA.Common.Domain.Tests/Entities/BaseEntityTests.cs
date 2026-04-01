using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class BaseEntityTests
{
    private sealed class TestEntity : BaseEntity<int>;

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
}
