using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Users;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the generic soft-deleted user check (BR-133): one query, filters bypassed, matching only
/// rows that exist AND are soft-deleted.
/// </summary>
public sealed class SoftDeletedUserValidatorTests
{
    [Fact]
    public async Task IsUserSoftDeletedAsync_QueriesOnceWithQueryFiltersIgnored()
    {
        var (sut, repository) = CreateSut(exists: true);

        var isDeleted = await sut.IsUserSoftDeletedAsync(42);

        isDeleted.Should().BeTrue();
        repository.Verify(
            x => x.ExistsAsync(
                It.IsAny<Expression<Func<TestIdentityUser, bool>>>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the soft-delete global query filter has to be bypassed to see the deleted row");
    }

    [Fact]
    public async Task IsUserSoftDeletedAsync_WhenNoMatchingRow_ReturnsFalse()
    {
        var (sut, _) = CreateSut(exists: false);

        var isDeleted = await sut.IsUserSoftDeletedAsync(42);

        isDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserSoftDeletedAsync_PredicateMatchesOnlyTheDeletedTargetRow()
    {
        Expression<Func<TestIdentityUser, bool>>? captured = null;
        var (sut, repository) = CreateSut(exists: true);
        repository
            .Setup(x => x.ExistsAsync(
                It.IsAny<Expression<Func<TestIdentityUser, bool>>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback((Expression<Func<TestIdentityUser, bool>> where, bool _, CancellationToken _) => captured = where)
            .ReturnsAsync(true);

        await sut.IsUserSoftDeletedAsync(42);

        captured.Should().NotBeNull();
        var predicate = captured!.Compile();
        var deletedTarget = new TestIdentityUser { Id = 42 };
        deletedTarget.Delete();
        predicate(deletedTarget).Should().BeTrue();
        predicate(new TestIdentityUser { Id = 42 }).Should().BeFalse("a live account is not soft-deleted");

        var otherDeleted = new TestIdentityUser { Id = 43 };
        otherDeleted.Delete();
        predicate(otherDeleted).Should().BeFalse("another user's deleted row must not match");
    }

    private static (SoftDeletedUserValidator<TestIdentityUser> Sut, Mock<IRepository<TestIdentityUser, UserIdentifierType>> Repository)
        CreateSut(bool exists)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestIdentityUser, UserIdentifierType>>();

        unitOfWork.Setup(x => x.GetRepository<TestIdentityUser, UserIdentifierType>()).Returns(repository.Object);
        repository
            .Setup(x => x.ExistsAsync(
                It.IsAny<Expression<Func<TestIdentityUser, bool>>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(exists);

        return (new SoftDeletedUserValidator<TestIdentityUser>(unitOfWork.Object), repository);
    }
}
