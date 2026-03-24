using FluentAssertions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
namespace MMCA.Common.Domain.Tests.Entities;

public class AuditableAggregateRootEntityTests
{
    private sealed record TestDomainEvent(string Data) : BaseDomainEvent;

    private sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    // ── AddDomainEvent ──
    [Fact]
    public void AddDomainEvent_AddsEventToCollection()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var evt = new TestDomainEvent("test");

        aggregate.AddDomainEvent(evt);

        aggregate.DomainEvents.Should().ContainSingle()
            .Which.Should().Be(evt);
    }

    [Fact]
    public void AddDomainEvent_MultipleEvents_AddsAll()
    {
        var aggregate = new TestAggregate { Id = 1 };

        aggregate.AddDomainEvent(new TestDomainEvent("A"));
        aggregate.AddDomainEvent(new TestDomainEvent("B"));

        aggregate.DomainEvents.Should().HaveCount(2);
    }

    [Fact]
    public void AddDomainEvent_WithNull_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => new TestAggregate { Id = 1 }.AddDomainEvent(null!))
            .Should().Throw<ArgumentNullException>();

    // ── ClearDomainEvents ──
    [Fact]
    public void ClearDomainEvents_RemovesAllEvents()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddDomainEvent(new TestDomainEvent("A"));
        aggregate.AddDomainEvent(new TestDomainEvent("B"));

        aggregate.ClearDomainEvents();

        aggregate.DomainEvents.Should().BeEmpty();
    }

    // ── DomainEvents starts empty ──
    [Fact]
    public void DomainEvents_InitiallyEmpty()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.DomainEvents.Should().BeEmpty();
    }

    // ── Delete (inherited) ──
    [Fact]
    public void Delete_SetsIsDeletedTrue()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var result = aggregate.Delete();

        result.IsSuccess.Should().BeTrue();
        aggregate.IsDeleted.Should().BeTrue();
    }
}
