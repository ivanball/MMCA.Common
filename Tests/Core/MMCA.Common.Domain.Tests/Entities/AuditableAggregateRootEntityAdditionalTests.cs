using AwesomeAssertions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class AuditableAggregateRootEntityAdditionalTests
{
    /// <summary>The aggregate's own code for "this candidate is not soft-deleted".</summary>
    private const string NotDeletedCode = "Test.Restorable.NotDeleted";

    private sealed class ChildEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>A child whose own rules refuse the delete, so the cascade has a failure to aggregate.</summary>
    private sealed class UndeletableChildEntity : AuditableBaseEntity<int>
    {
        public override Shared.Abstractions.Result Delete() =>
            Shared.Abstractions.Result.Failure(
                Shared.Abstractions.Error.Invariant("Child.Locked", "This child cannot be deleted."));
    }

    /// <summary>A child that publishes the reactivation decision, the shape a restorable child takes.</summary>
    private sealed class ReactivatableChildEntity : AuditableBaseEntity<int>, Domain.Interfaces.IReactivatable
    {
        public string Name { get; set; } = string.Empty;

        public Shared.Abstractions.Result Reactivate() => Undelete();
    }

    private sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        private readonly List<ChildEntity> _children = [];
        private readonly List<ReactivatableChildEntity> _restorables = [];

        public void ReplaceChildren(IEnumerable<ChildEntity> items) => SetItems(_children, items);

        public Shared.Abstractions.Result<ChildEntity> FindChild(int childId) =>
            GetChildOrNotFound<ChildEntity, int>(_children, childId, nameof(FindChild));

        public void AddChild(ChildEntity child) => _children.Add(child);

        public int ChildCount => _children.Count;

        public IReadOnlyList<ChildEntity> Children => _children;

        public Shared.Abstractions.Result CascadeDelete() =>
            DeleteChildren<ChildEntity, int>(_children);

        public static Shared.Abstractions.Result CascadeDelete(IEnumerable<UndeletableChildEntity> children) =>
            DeleteChildren<UndeletableChildEntity, int>(children);

        public Shared.Abstractions.Result<ChildEntity> DropChild(int childId) =>
            RemoveChildOrNotFound<ChildEntity, int>(_children, childId, nameof(DropChild));

        /// <summary>The plain lookup under the SAME source, so the two failures can be compared directly.</summary>
        public Shared.Abstractions.Result<ChildEntity> FindChildAsDropChild(int childId) =>
            GetChildOrNotFound<ChildEntity, int>(_children, childId, nameof(DropChild));

        public static Shared.Abstractions.Result<UndeletableChildEntity> DropLockedChild(
            IEnumerable<UndeletableChildEntity> children,
            int childId) =>
            RemoveChildOrNotFound<UndeletableChildEntity, int>(children, childId, nameof(DropLockedChild));

        public void AddRestorable(ReactivatableChildEntity child) => _restorables.Add(child);

        public IReadOnlyList<ReactivatableChildEntity> Restorables => _restorables;

        public Shared.Abstractions.Result<ReactivatableChildEntity> Restore(ReactivatableChildEntity child) =>
            RestoreChild<ReactivatableChildEntity, int>(
                _restorables, child, NotDeletedCode, nameof(Restore));
    }

    private sealed class ValidatingAggregate : AuditableAggregateRootEntity<int>
    {
        private readonly List<ChildEntity> _children = [];

        public void ReplaceChildren(IEnumerable<ChildEntity> items) => SetItems(_children, items);

        protected override void ValidateSetItems<TChildEntity>(
            IList<TChildEntity> currentItems,
            IList<TChildEntity> incomingItems)
        {
            if (incomingItems.Count == 0)
            {
                throw new InvalidOperationException("Cannot clear children");
            }
        }
    }

    // ── SetItems ──
    [Fact]
    public void SetItems_ReplacesCollection()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 1, Name = "Original" });

        aggregate.ReplaceChildren([new ChildEntity { Id = 2, Name = "Replacement" }]);

        aggregate.ChildCount.Should().Be(1);
    }

    [Fact]
    public void SetItems_WithEmptyCollection_ClearsChildren()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 1, Name = "A" });
        aggregate.AddChild(new ChildEntity { Id = 2, Name = "B" });

        aggregate.ReplaceChildren([]);

        aggregate.ChildCount.Should().Be(0);
    }

    [Fact]
    public void SetItems_WithNullCollection_ThrowsArgumentNullException()
    {
        var aggregate = new TestAggregate { Id = 1 };

        var act = () => aggregate.ReplaceChildren(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetItems_CallsValidateSetItems_WhichCanReject()
    {
        var aggregate = new ValidatingAggregate { Id = 1 };

        var act = () => aggregate.ReplaceChildren([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot clear children");
    }

    [Fact]
    public void SetItems_WithMultipleItems_SetsAll()
    {
        var aggregate = new TestAggregate { Id = 1 };

        aggregate.ReplaceChildren(
        [
            new ChildEntity { Id = 1, Name = "A" },
            new ChildEntity { Id = 2, Name = "B" },
            new ChildEntity { Id = 3, Name = "C" }
        ]);

        aggregate.ChildCount.Should().Be(3);
    }

    // ── GetChildOrNotFound ──
    [Fact]
    public void GetChildOrNotFound_ExistingChild_ReturnsSuccess()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 10, Name = "Child10" });

        var result = aggregate.FindChild(10);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Child10");
    }

    [Fact]
    public void GetChildOrNotFound_NonExistingChild_ReturnsNotFound()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 10, Name = "Child10" });

        var result = aggregate.FindChild(999);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Type.Should().Be(Shared.Abstractions.ErrorType.NotFound);
    }

    [Fact]
    public void GetChildOrNotFound_DeletedChild_ReturnsNotFound()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ChildEntity { Id = 10, Name = "Deleted" };
        child.Delete();
        aggregate.AddChild(child);

        var result = aggregate.FindChild(10);

        result.IsFailure.Should().BeTrue();
    }

    // ── DeleteChildren ──
    [Fact]
    public void DeleteChildren_SoftDeletesEveryActiveChild()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 1, Name = "A" });
        aggregate.AddChild(new ChildEntity { Id = 2, Name = "B" });

        var result = aggregate.CascadeDelete();

        result.IsSuccess.Should().BeTrue();
        aggregate.Children.Should().OnlyContain(c => c.IsDeleted);
        aggregate.ChildCount.Should().Be(2, "a cascade soft-deletes, it never removes rows from the collection");
    }

    [Fact]
    public void DeleteChildren_SkipsAlreadyDeletedChildren()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var alreadyGone = new ChildEntity { Id = 1, Name = "A" };
        alreadyGone.Delete();
        aggregate.AddChild(alreadyGone);
        aggregate.AddChild(new ChildEntity { Id = 2, Name = "B" });

        var result = aggregate.CascadeDelete();

        result.IsSuccess.Should().BeTrue(
            "an already-deleted child is skipped, not reported as an Error.AlreadyDeleted the caller cannot act on");
        aggregate.Children.Should().OnlyContain(c => c.IsDeleted);
    }

    [Fact]
    public void DeleteChildren_WithNoChildren_ReturnsSuccess() =>
        new TestAggregate { Id = 1 }.CascadeDelete().IsSuccess.Should().BeTrue();

    [Fact]
    public void DeleteChildren_IsIdempotentAcrossRepeatedCascades()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 1, Name = "A" });

        aggregate.CascadeDelete().IsSuccess.Should().BeTrue();

        aggregate.CascadeDelete().IsSuccess.Should().BeTrue(
            "re-deleting a parent must not fail on children that the first cascade already deleted");
    }

    [Fact]
    public void DeleteChildren_AggregatesEveryChildFailure()
    {
        UndeletableChildEntity[] children =
        [
            new() { Id = 1 },
            new() { Id = 2 },
        ];

        var result = TestAggregate.CascadeDelete(children);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2, "Result.Combine carries every child's error, not just the first");
        result.Errors.Should().OnlyContain(e => e.Code == "Child.Locked");
    }

    [Fact]
    public void DeleteChildren_WithNullCollection_ThrowsArgumentNullException()
    {
        var act = () => TestAggregate.CascadeDelete(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── RemoveChildOrNotFound ──
    [Fact]
    public void RemoveChildOrNotFound_ActiveChild_SoftDeletesItAndHandsItBack()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ChildEntity { Id = 10, Name = "Doomed" };
        aggregate.AddChild(child);

        var result = aggregate.DropChild(10);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(child, "the caller needs the child to raise its own domain event");
        child.IsDeleted.Should().BeTrue();
        aggregate.ChildCount.Should().Be(1, "a removal soft-deletes, it never drops the row from the collection");
    }

    [Fact]
    public void RemoveChildOrNotFound_RaisesNoDomainEvent()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 10, Name = "Doomed" });

        aggregate.DropChild(10).IsSuccess.Should().BeTrue();

        aggregate.DomainEvents.Should().BeEmpty(
            "which event a removal raises is aggregate vocabulary, so event raising stays with the caller");
    }

    [Fact]
    public void RemoveChildOrNotFound_MissingChild_ProducesTheSameFailureAsTheLookup()
    {
        var aggregate = new TestAggregate { Id = 1 };
        aggregate.AddChild(new ChildEntity { Id = 10, Name = "Present" });

        var removeResult = aggregate.DropChild(999);
        var lookupResult = aggregate.FindChildAsDropChild(999);

        removeResult.IsFailure.Should().BeTrue();
        removeResult.Errors.Should().BeEquivalentTo(
            lookupResult.Errors,
            "swapping a hand-written get-then-delete pair for the helper must be behavior-preserving");
        removeResult.Errors.Should().ContainSingle()
            .Which.Type.Should().Be(Shared.Abstractions.ErrorType.NotFound);
    }

    [Fact]
    public void RemoveChildOrNotFound_AlreadyDeletedChild_ReturnsNotFound()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ChildEntity { Id = 10, Name = "Gone" };
        child.Delete();
        aggregate.AddChild(child);

        var result = aggregate.DropChild(10);

        result.IsFailure.Should().BeTrue(
            "the lookup only sees ACTIVE children, so a second removal is NotFound rather than AlreadyDeleted");
        result.Errors.Should().ContainSingle()
            .Which.Type.Should().Be(Shared.Abstractions.ErrorType.NotFound);
    }

    [Fact]
    public void RemoveChildOrNotFound_ChildRefusesTheDelete_ReturnsThatFailure()
    {
        UndeletableChildEntity[] children = [new() { Id = 7 }];

        var result = TestAggregate.DropLockedChild(children, 7);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(
            "Child.Locked",
            "the child's own rule is reported, not swallowed behind a generic failure");
    }

    // ── RestoreChild ──
    [Fact]
    public void RestoreChild_SoftDeletedChildOutsideTheCollection_ReactivatesAndAddsIt()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ReactivatableChildEntity { Id = 10, Name = "Back" };
        child.Delete();

        var result = aggregate.Restore(child);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(child);
        child.IsDeleted.Should().BeFalse();
        aggregate.Restorables.Should().ContainSingle().Which.Should().BeSameAs(child,
            "a soft-deleted row is excluded by the query filter, so the caller resolves it and the helper adds it back");
    }

    [Fact]
    public void RestoreChild_ChildAlreadyInTheCollection_DoesNotDuplicateIt()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ReactivatableChildEntity { Id = 10, Name = "Back" };
        child.Delete();
        aggregate.AddRestorable(child);

        aggregate.Restore(child).IsSuccess.Should().BeTrue();

        aggregate.Restorables.Should().HaveCount(1);
    }

    [Fact]
    public void RestoreChild_ChildThatIsNotSoftDeleted_FailsWithTheSuppliedCode()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ReactivatableChildEntity { Id = 10, Name = "Live" };

        var result = aggregate.Restore(child);

        result.IsFailure.Should().BeTrue();

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Test.Restorable.NotDeleted", "the code is consumer vocabulary the framework must not invent");
        error.Type.Should().Be(Shared.Abstractions.ErrorType.Invariant);
        error.Source.Should().Be("Restore");
        error.Target.Should().Be(nameof(ReactivatableChildEntity));
        aggregate.Restorables.Should().BeEmpty("a rejected restore adds nothing");
    }

    [Fact]
    public void RestoreChild_RaisesNoDomainEvent()
    {
        var aggregate = new TestAggregate { Id = 1 };
        var child = new ReactivatableChildEntity { Id = 10, Name = "Back" };
        child.Delete();

        aggregate.Restore(child).IsSuccess.Should().BeTrue();

        aggregate.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void RestoreChild_WithNullChild_ThrowsArgumentNullException()
    {
        var aggregate = new TestAggregate { Id = 1 };

        var act = () => aggregate.Restore(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
