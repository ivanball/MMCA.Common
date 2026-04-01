using AwesomeAssertions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class AuditableAggregateRootEntityAdditionalTests
{
    private sealed class ChildEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestAggregate : AuditableAggregateRootEntity<int>
    {
        private readonly List<ChildEntity> _children = [];

        public void ReplaceChildren(IEnumerable<ChildEntity> items) => SetItems(_children, items);

        public Shared.Abstractions.Result<ChildEntity> FindChild(int childId) =>
            GetChildOrNotFound<ChildEntity, int>(_children, childId, nameof(FindChild));

        public void AddChild(ChildEntity child) => _children.Add(child);

        public int ChildCount => _children.Count;
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
}
