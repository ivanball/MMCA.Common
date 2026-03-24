using System.Linq.Expressions;
using FluentAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class NavigationLoaderTests
{
    // ── LoadFKPropertyAsync ──
    [Fact]
    public async Task LoadFKPropertyAsync_WhenParentIdsExist_QueriesRepositoryAndAssigns()
    {
        var parent = new StubParent { Id = 1, ChildFKId = 10 };
        IReadOnlyCollection<StubParent> parents = [parent];

        var child = new StubChild { Id = 10 };
        var repo = new Mock<IReadRepository<StubChild, int>>();
        repo.Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([child]);

        List<StubChild>? assigned = null;

        await NavigationLoader.LoadFKPropertyAsync<StubParent, StubChild, int>(
            parents,
            p => p.ChildFKId,
            c => c.Id,
            repo.Object,
            (p, list) => assigned = list,
            CancellationToken.None);

        assigned.Should().NotBeNull();
        assigned.Should().ContainSingle().Which.Id.Should().Be(10);
    }

    [Fact]
    public async Task LoadFKPropertyAsync_WhenAllParentKeysAreNull_AssignsEmptyLists()
    {
        var parent = new StubParent { Id = 1, ChildFKId = null };
        IReadOnlyCollection<StubParent> parents = [parent];

        var repo = new Mock<IReadRepository<StubChild, int>>();
        List<StubChild>? assigned = null;

        await NavigationLoader.LoadFKPropertyAsync<StubParent, StubChild, int>(
            parents,
            p => p.ChildFKId,
            c => c.Id,
            repo.Object,
            (p, list) => assigned = list,
            CancellationToken.None);

        assigned.Should().NotBeNull();
        assigned.Should().BeEmpty();
        repo.Verify(
            r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadFKPropertyAsync_WhenNoChildrenMatch_AssignsEmptyList()
    {
        var parent = new StubParent { Id = 1, ChildFKId = 99 };
        IReadOnlyCollection<StubParent> parents = [parent];

        var repo = new Mock<IReadRepository<StubChild, int>>();
        repo.Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        List<StubChild>? assigned = null;

        await NavigationLoader.LoadFKPropertyAsync<StubParent, StubChild, int>(
            parents,
            p => p.ChildFKId,
            c => c.Id,
            repo.Object,
            (p, list) => assigned = list,
            CancellationToken.None);

        assigned.Should().NotBeNull();
        assigned.Should().BeEmpty();
    }

    // ── LoadChildrenPropertyAsync ──
    [Fact]
    public async Task LoadChildrenPropertyAsync_WhenParentsExist_QueriesAndAssignsChildren()
    {
        var parent = new StubParent { Id = 1 };
        IReadOnlyCollection<StubParent> parents = [parent];

        var child = new StubChild { Id = 10, ParentId = 1 };
        var repo = new Mock<IReadRepository<StubChild, int>>();
        repo.Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([child]);

        List<StubChild>? assigned = null;

        await NavigationLoader.LoadChildrenPropertyAsync<StubParent, int, StubChild, int>(
            parents,
            p => p.Id,
            c => c.ParentId,
            repo.Object,
            (p, list) => assigned = list,
            CancellationToken.None);

        assigned.Should().NotBeNull();
        assigned.Should().ContainSingle().Which.ParentId.Should().Be(1);
    }

    [Fact]
    public async Task LoadChildrenPropertyAsync_WhenNoParents_AssignsEmptyLists()
    {
        IReadOnlyCollection<StubParent> parents = [];
        var repo = new Mock<IReadRepository<StubChild, int>>();

        await NavigationLoader.LoadChildrenPropertyAsync<StubParent, int, StubChild, int>(
            parents,
            p => p.Id,
            c => c.ParentId,
            repo.Object,
            (_, _) => { },
            CancellationToken.None);

        repo.Verify(
            r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadChildrenPropertyAsync_WhenChildHasNoMatchingParent_AssignsEmptyList()
    {
        var parent = new StubParent { Id = 1 };
        IReadOnlyCollection<StubParent> parents = [parent];

        var repo = new Mock<IReadRepository<StubChild, int>>();
        repo.Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Expression<Func<StubChild, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        List<StubChild>? assigned = null;

        await NavigationLoader.LoadChildrenPropertyAsync<StubParent, int, StubChild, int>(
            parents,
            p => p.Id,
            c => c.ParentId,
            repo.Object,
            (p, list) => assigned = list,
            CancellationToken.None);

        assigned.Should().NotBeNull();
        assigned.Should().BeEmpty();
    }
}

// ── Test entity types (must be public for Moq DynamicProxy) ──
public class StubParent : AuditableBaseEntity<int>
{
    public int? ChildFKId { get; set; }
}

public class StubChild : AuditableBaseEntity<int>
{
    public int ParentId { get; set; }
}
