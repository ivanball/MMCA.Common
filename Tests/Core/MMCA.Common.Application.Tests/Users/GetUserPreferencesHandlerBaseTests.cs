using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Users.UseCases.GetPreferences;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared preference-read workflow (ADR-027 / ADR-028), including the read-path choice:
/// the query resolves the READ repository, never the write repository.
/// </summary>
public sealed class GetUserPreferencesHandlerBaseTests
{
    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFoundWithHandlerSourceAndEntityTarget()
    {
        var (sut, mocks) = CreateSut();
        mocks.ReadRepository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestIdentityUser?)null);

        Result<UserPreferencesResponse> result = await sut.HandleAsync(new GetUserPreferencesQuery(404));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Type == ErrorType.NotFound
            && e.Source == nameof(TestGetUserPreferencesHandler)
            && e.Target == nameof(TestIdentityUser));
    }

    [Fact]
    public async Task HandleAsync_WhenUserExists_ProjectsStoredPreferences()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser { Id = 1 };
        user.SeedPreferences("es", "dark");
        mocks.ReadRepository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Result<UserPreferencesResponse> result = await sut.HandleAsync(new GetUserPreferencesQuery(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Culture.Should().Be("es");
        result.Value.Theme.Should().Be("dark");
    }

    [Fact]
    public async Task HandleAsync_UsesReadRepositoryNotWriteRepository()
    {
        var (sut, mocks) = CreateSut();
        mocks.ReadRepository
            .Setup(x => x.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestIdentityUser { Id = 1 });

        await sut.HandleAsync(new GetUserPreferencesQuery(1));

        mocks.UnitOfWork.Verify(
            x => x.GetReadRepository<TestIdentityUser, UserIdentifierType>(),
            Times.Once,
            "a query handler never saves, so it takes the read path");
        mocks.UnitOfWork.Verify(
            x => x.GetRepository<TestIdentityUser, UserIdentifierType>(),
            Times.Never);
    }

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IReadRepository<TestIdentityUser, UserIdentifierType>> ReadRepository);

    private static (TestGetUserPreferencesHandler Sut, HandlerMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepository = new Mock<IReadRepository<TestIdentityUser, UserIdentifierType>>();

        unitOfWork.Setup(x => x.GetReadRepository<TestIdentityUser, UserIdentifierType>()).Returns(readRepository.Object);

        var sut = new TestGetUserPreferencesHandler(unitOfWork.Object);
        return (sut, new HandlerMocks(unitOfWork, readRepository));
    }
}

/// <summary>Concrete subclass standing in for an app's <c>GetUserPreferencesHandler</c>.</summary>
public sealed class TestGetUserPreferencesHandler(IUnitOfWork unitOfWork)
    : GetUserPreferencesHandlerBase<TestIdentityUser>(unitOfWork);
