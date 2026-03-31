using AwesomeAssertions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Enums;

namespace MMCA.Common.Domain.Tests.DomainEvents;

public sealed class EntityChangedEventTests
{
    // ── Created state ──
    [Fact]
    public void Create_WithAddedState_StoresCorrectState()
    {
        var evt = new TestEntityChangedEvent(DomainEntityState.Added, 42);

        evt.State.Should().Be(DomainEntityState.Added);
        evt.EntityId.Should().Be(42);
    }

    // ── Updated state ──
    [Fact]
    public void Create_WithUpdatedState_StoresCorrectState()
    {
        var evt = new TestEntityChangedEvent(DomainEntityState.Updated, 99);

        evt.State.Should().Be(DomainEntityState.Updated);
        evt.EntityId.Should().Be(99);
    }

    // ── Deleted state ──
    [Fact]
    public void Create_WithDeletedState_StoresCorrectState()
    {
        var evt = new TestEntityChangedEvent(DomainEntityState.Deleted, 7);

        evt.State.Should().Be(DomainEntityState.Deleted);
        evt.EntityId.Should().Be(7);
    }

    // ── DateOccurred is set ──
    [Fact]
    public void Create_HasDateOccurred()
    {
        var before = DateTime.UtcNow;
        var evt = new TestEntityChangedEvent(DomainEntityState.Added, 1);
        var after = DateTime.UtcNow;

        evt.DateOccurred.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── Guid entity ID ──
    [Fact]
    public void Create_WithGuidEntityId_StoresCorrectly()
    {
        var id = Guid.NewGuid();
        var evt = new TestGuidEntityChangedEvent(DomainEntityState.Added, id);

        evt.EntityId.Should().Be(id);
        evt.State.Should().Be(DomainEntityState.Added);
    }
}

// ── Test helpers ──
public sealed record TestEntityChangedEvent(
    DomainEntityState State,
    int EntityId) : EntityChangedEvent<int>(State, EntityId);

public sealed record TestGuidEntityChangedEvent(
    DomainEntityState State,
    Guid EntityId) : EntityChangedEvent<Guid>(State, EntityId);
