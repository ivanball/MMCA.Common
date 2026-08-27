using AwesomeAssertions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class AuditableAggregateRootEntityAdditionalTests
{
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

    private sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        private readonly List<ChildEntity> _children = [];

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
}
