using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Pins the view-as read-only guarantee at the command level (ADR-137): while the caller's token
/// carries the view-as actor claim, no command reaches its handler, whatever the command is.
/// </summary>
public sealed class ViewAsCommandDecoratorTests
{
    [Fact]
    public async Task HandleAsync_ViewAsCaller_RefusesWithoutCallingInner()
    {
        var inner = new Mock<ICommandHandler<PlainCommand, Result>>();
        var sut = new ViewAsCommandDecorator<PlainCommand, Result>(inner.Object, CurrentUser(viewAs: true));

        var result = await sut.HandleAsync(new PlainCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Should().Match<Error>(e => e.Code == "Auth.ViewAsReadOnly" && e.Type == ErrorType.Forbidden);
        inner.Verify(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ViewAsCaller_RefusesTypedResultCommands()
    {
        var inner = new Mock<ICommandHandler<TypedCommand, Result<int>>>();
        var sut = new ViewAsCommandDecorator<TypedCommand, Result<int>>(inner.Object, CurrentUser(viewAs: true));

        var result = await sut.HandleAsync(new TypedCommand());

        result.IsFailure.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<TypedCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_OrdinaryCaller_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<PlainCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = new ViewAsCommandDecorator<PlainCommand, Result>(inner.Object, CurrentUser(viewAs: false));

        var result = await sut.HandleAsync(new PlainCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ICurrentUserService CurrentUser(bool viewAs)
    {
        List<Claim> claims = [new("sub", "42")];
        if (viewAs)
        {
            claims.Add(new Claim(AuthClaimTypes.ViewAsActor, "7"));
        }

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt")));
        return currentUser.Object;
    }

    public sealed record PlainCommand : ICommand<Result>;

    public sealed record TypedCommand : ICommand<Result<int>>;
}
