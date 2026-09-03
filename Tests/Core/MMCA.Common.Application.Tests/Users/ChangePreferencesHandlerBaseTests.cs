using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Users.UseCases.ChangePreferences;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared preference-write workflow (ADR-027 / ADR-028), above all the partial-update
/// rule: a null field falls back to the stored value, so the culture switcher and the theme toggle
/// never clobber each other.
/// </summary>
public sealed class ChangePreferencesHandlerBaseTests
{
    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFoundWithoutSaving()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestIdentityUser?)null);

        Result result = await sut.HandleAsync(new TestChangePreferencesCommand(404, new ChangePreferencesRequest("es", null)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Type == ErrorType.NotFound
            && e.Source == nameof(TestChangePreferencesHandler)
            && e.Target == nameof(TestIdentityUser));
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyCultureSupplied_KeepsStoredTheme()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser { Id = 1 };
        user.SeedPreferences("en", "dark");
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestChangePreferencesCommand(1, new ChangePreferencesRequest("es", null)));

        result.IsSuccess.Should().BeTrue();
        user.PreferredCulture.Should().Be("es");
        user.PreferredTheme.Should().Be("dark", "a null request field leaves that preference unchanged");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyThemeSupplied_KeepsStoredCulture()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser { Id = 1 };
        user.SeedPreferences("es", "light");
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestChangePreferencesCommand(1, new ChangePreferencesRequest(null, "dark")));

        result.IsSuccess.Should().BeTrue();
        user.PreferredCulture.Should().Be("es");
        user.PreferredTheme.Should().Be("dark");
    }

    [Fact]
    public async Task HandleAsync_WhenAggregateRejectsUpdate_ReturnsFailureWithoutSaving()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser
        {
            Id = 1,
            ForcedFailure = Error.Invariant("User.PreferredCulture.Invalid", "Culture is not supported."),
        };
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestChangePreferencesCommand(1, new ChangePreferencesRequest("zz", null)));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.PreferredCulture.Invalid");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void ArrangeUser(HandlerMocks mocks, TestIdentityUser user) =>
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestIdentityUser, UserIdentifierType>> Repository);

    private static (TestChangePreferencesHandler Sut, HandlerMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestIdentityUser, UserIdentifierType>>();

        unitOfWork.Setup(x => x.GetRepository<TestIdentityUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = new TestChangePreferencesHandler(unitOfWork.Object);
        return (sut, new HandlerMocks(unitOfWork, repository));
    }
}

/// <summary>Concrete subclass standing in for an app's <c>ChangePreferencesHandler</c>.</summary>
public sealed class TestChangePreferencesHandler(IUnitOfWork unitOfWork)
    : ChangePreferencesHandlerBase<TestIdentityUser, TestChangePreferencesCommand>(unitOfWork, NullLogger.Instance);
