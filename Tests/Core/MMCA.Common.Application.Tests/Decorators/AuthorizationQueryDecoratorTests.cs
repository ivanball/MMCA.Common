using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class AuthorizationQueryDecoratorTests
{
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IPermissionRegistry> _permissionRegistry = new();

    public AuthorizationQueryDecoratorTests() =>
        _currentUser.Setup(x => x.Roles).Returns(["Attendee"]);

    // ── A query without the marker is never checked at all ──
    [Fact]
    public async Task HandleAsync_QueryWithoutPermission_DelegatesToInner()
    {
        var inner = new Mock<IQueryHandler<UnguardedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnguardedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("ok"));

        var sut = new AuthorizationQueryDecorator<UnguardedQuery, Result<string>>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new UnguardedQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
        _permissionRegistry.Verify(
            x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Granted permission passes through ──
    [Fact]
    public async Task HandleAsync_WhenPermissionGranted_DelegatesToInner()
    {
        var inner = new Mock<IQueryHandler<GuardedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<GuardedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("ok"));
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), "catalog.products.read"))
            .Returns(true);

        var sut = new AuthorizationQueryDecorator<GuardedQuery, Result<string>>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedQuery());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<GuardedQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Denied permission short-circuits with Forbidden and never reaches the handler ──
    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_ReturnsForbiddenWithoutCallingInner()
    {
        var inner = new Mock<IQueryHandler<GuardedQuery, Result<string>>>();
        _permissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new AuthorizationQueryDecorator<GuardedQuery, Result<string>>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new GuardedQuery());

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Authorization.PermissionDenied");
        error.Type.Should().Be(ErrorType.Forbidden);
        error.Source.Should().Be(nameof(GuardedQuery));
        inner.Verify(x => x.HandleAsync(It.IsAny<GuardedQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A handler whose TResult is neither Result nor Result<T> is decorated too and must not fail on
    // resolve; it only fails if it ever needs to fabricate a failure.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnguardedQuery_PassesThrough()
    {
        var inner = new Mock<IQueryHandler<UnguardedQuery, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UnguardedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var sut = new AuthorizationQueryDecorator<UnguardedQuery, string>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new UnguardedQuery());

        result.Should().Be("handled");
    }
}

// ── Test types ──
public sealed record UnguardedQuery;

public sealed record GuardedQuery : IRequiresPermission
{
    public string Permission => "catalog.products.read";
}
