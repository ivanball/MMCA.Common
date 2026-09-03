using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class AuthorizationCommandDecoratorTests
{
    private static readonly string[] MultipleRoles = ["Organizer", "Attendee"];

    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IPermissionRegistry> _permissionRegistry = new();

    public AuthorizationCommandDecoratorTests() =>
        _currentUser.Setup(x => x.Roles).Returns(["Attendee"]);

    // ── A command without the marker is never checked at all ──
    [Fact]
    public async Task HandleAsync_CommandWithoutPermission_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<UnguardedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnguardedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new AuthorizationCommandDecorator<UnguardedCommand, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new UnguardedCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<UnguardedCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        _permissionRegistry.Verify(
            x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Granted permission passes through ──
    [Fact]
    public async Task HandleAsync_WhenPermissionGranted_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<GuardedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), "catalog.products.write"))
            .Returns(true);

        var sut = new AuthorizationCommandDecorator<GuardedCommand, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Denied permission short-circuits with Forbidden and never reaches the handler ──
    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_ReturnsForbiddenWithoutCallingInner()
    {
        var inner = new Mock<ICommandHandler<GuardedCommand, Result>>();
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new AuthorizationCommandDecorator<GuardedCommand, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedCommand());

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Authorization.PermissionDenied");
        error.Type.Should().Be(ErrorType.Forbidden);
        error.Source.Should().Be(nameof(GuardedCommand));
        error.Message.Should().Contain("catalog.products.write");
        inner.Verify(x => x.HandleAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── The caller's roles, not a hard-coded set, are what the registry is asked about ──
    [Fact]
    public async Task HandleAsync_ChecksThePermissionAgainstTheCurrentUsersRoles()
    {
        var inner = new Mock<ICommandHandler<GuardedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _currentUser.Setup(x => x.Roles).Returns(MultipleRoles);
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new AuthorizationCommandDecorator<GuardedCommand, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        await sut.HandleAsync(new GuardedCommand());

        _permissionRegistry.Verify(
            x => x.HasPermission(
                It.Is<IEnumerable<string>>(roles => roles.SequenceEqual(MultipleRoles)),
                "catalog.products.write"),
            Times.Once);
    }

    // ── The failure factory also serves Result<T> ──
    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_WithGenericResult_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<GuardedCommandWithValue, Result<int>>>();
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new AuthorizationCommandDecorator<GuardedCommandWithValue, Result<int>>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedCommandWithValue());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Forbidden);
        inner.Verify(
            x => x.HandleAsync(It.IsAny<GuardedCommandWithValue>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Scrutor's TryDecorate is unconditional, so a handler whose TResult is neither Result nor
    // Result<T> gets decorated too. Building the failure delegate eagerly would turn that into a
    // TypeInitializationException at RESOLVE time, even for a command that is never denied.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnguardedCommand_PassesThrough()
    {
        var inner = new Mock<ICommandHandler<UnguardedCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnguardedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var sut = new AuthorizationCommandDecorator<UnguardedCommand, string>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new UnguardedCommand());

        result.Should().Be("handled");
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
