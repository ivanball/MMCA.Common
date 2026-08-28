using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Extensions;

/// <summary>
/// <c>RequireUserId</c> collapses the read-then-null-check-then-fail block that every handler and
/// controller guarding a per-user operation repeats. The default classification is
/// <see cref="ErrorType.Forbidden"/>, matching what the handler-side copies report today.
/// </summary>
public sealed class CurrentUserServiceExtensionsTests
{
    [Fact]
    public void RequireUserId_WhenAuthenticated_ReturnsTheIdentifier()
    {
        ICurrentUserService currentUserService = CurrentUser(42);

        Result<UserIdentifierType> result = currentUserService.RequireUserId("CheckIns.Forbidden");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void RequireUserId_WhenUnauthenticated_ReturnsForbiddenFailure()
    {
        ICurrentUserService currentUserService = CurrentUser(null);

        Result<UserIdentifierType> result = currentUserService.RequireUserId("CheckIns.Forbidden");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Code.Should().Be("CheckIns.Forbidden");
        result.Errors[0].Message.Should().Be("Access denied.");
        result.Errors[0].Type.Should().Be(ErrorType.Forbidden);
        result.Errors[0].Source.Should().BeNull();
    }

    [Fact]
    public void RequireUserId_WhenUnauthenticated_CarriesTheSuppliedMessageTypeAndSource()
    {
        ICurrentUserService currentUserService = CurrentUser(null);

        Result<UserIdentifierType> result = currentUserService.RequireUserId(
            "Users.Unauthorized",
            "Authentication is required.",
            ErrorType.Unauthorized,
            "ExportUserDataAsync");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Users.Unauthorized");
        result.Errors[0].Message.Should().Be("Authentication is required.");
        result.Errors[0].Type.Should().Be(ErrorType.Unauthorized);
        result.Errors[0].Source.Should().Be("ExportUserDataAsync");
    }

    [Fact]
    public void RequireUserId_WithNullService_ThrowsArgumentNullException()
    {
        ICurrentUserService currentUserService = null!;

        FluentActions.Invoking(() => currentUserService.RequireUserId("Any.Forbidden"))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AccessDeniedMessage_MatchesTheMessageTheAppSideCopiesReport() =>
        CurrentUserServiceExtensions.AccessDeniedMessage.Should().Be("Access denied.");

    private static ICurrentUserService CurrentUser(UserIdentifierType? userId)
    {
        var mock = new Mock<ICurrentUserService>();
        mock.SetupGet(x => x.UserId).Returns(userId);
        mock.SetupGet(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity()));

        return mock.Object;
    }
}
