using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class AuthorizationCommandDecoratorTests : AuthorizationDecoratorTestsBase<Result>
{
    private static readonly string[] MultipleRoles = ["Manager", "Member"];

    protected override string GuardedPermission => "catalog.products.write";

    protected override string UnrelatedPermission => "catalog.products.read";

    protected override string GuardedRequestName => nameof(GuardedCommand);

    protected override Result SuccessResult => Result.Success();

    // ── Command only: the caller's roles, not a hard-coded set, are what the registry is asked about ──
    [Fact]
    public async Task HandleAsync_ChecksThePermissionAgainstTheCurrentUsersRoles()
    {
        var inner = new Mock<ICommandHandler<GuardedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        CurrentUser.Setup(x => x.Roles).Returns(MultipleRoles);
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new AuthorizationCommandDecorator<GuardedCommand, Result>(
            inner.Object, CurrentUser.Object, PermissionRegistry.Object);

        await sut.HandleAsync(new GuardedCommand());

        PermissionRegistry.Verify(
            x => x.HasPermission(
                It.Is<IEnumerable<string>>(roles => roles.SequenceEqual(MultipleRoles)),
                "catalog.products.write"),
            Times.Once);
    }

    // ── Command only: the failure factory also serves Result<T> ──
    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_WithGenericResult_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<GuardedCommandWithValue, Result<int>>>();
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new AuthorizationCommandDecorator<GuardedCommandWithValue, Result<int>>(
            inner.Object, CurrentUser.Object, PermissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedCommandWithValue());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Forbidden);
        inner.Verify(
            x => x.HandleAsync(It.IsAny<GuardedCommandWithValue>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    protected override Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner) => kind switch
    {
        RequestKind.Unguarded => Wrap(new UnguardedCommand(), inner),
        RequestKind.Guarded => Wrap(new GuardedCommand(), inner),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Func<Task<T>> Wrap<TCommand, T>(TCommand command, Func<Task<T>> inner)
        where TCommand : class
    {
        var handler = new Mock<ICommandHandler<TCommand, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new AuthorizationCommandDecorator<TCommand, T>(
            handler.Object, CurrentUser.Object, PermissionRegistry.Object);

        return () => sut.HandleAsync(command);
    }
}

// ── Test types ──
public sealed record UnguardedCommand;

public sealed record GuardedCommand : IRequiresPermission
{
    public string Permission => "catalog.products.write";
}

public sealed record GuardedCommandWithValue : IRequiresPermission
{
    public string Permission => "catalog.products.write";
}
