using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Verifies the <see cref="IRequiresMfa"/> gate the authorization decorators apply next to the
/// capability check: presence of the <c>mfa</c> claim allows, absence denies, and the capability
/// check still runs first so a caller who lacks the permission entirely is never told which use cases
/// additionally demand a step-up.
/// </summary>
public sealed class AuthorizationMultiFactorDecoratorTests
{
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IPermissionRegistry> _permissionRegistry = new();

    public AuthorizationMultiFactorDecoratorTests()
    {
        _currentUser.Setup(x => x.Roles).Returns(["Admin"]);
        _permissionRegistry
            .Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(true);
    }

    [Fact]
    public async Task Command_WithTheMultiFactorClaim_ReachesTheHandler()
    {
        WithPrincipal(new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp));
        var inner = InnerCommand();

        var result = await CreateCommandSut(inner).HandleAsync(new StepUpCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<StepUpCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Command_WithoutTheMultiFactorClaim_IsForbiddenAndNeverReachesTheHandler()
    {
        WithPrincipal();
        var inner = InnerCommand();

        var result = await CreateCommandSut(inner).HandleAsync(new StepUpCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Authorization.MultiFactorRequired" && e.Type == ErrorType.Forbidden);
        inner.Verify(x => x.HandleAsync(It.IsAny<StepUpCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Command_WithoutTheCapability_IsAnsweredByTheCapabilityGateFirst()
    {
        WithPrincipal();
        _permissionRegistry
            .Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);
        var inner = InnerCommand();

        var result = await CreateCommandSut(inner).HandleAsync(new StepUpCommand());

        result.Errors.Should().ContainSingle(e => e.Code == "Authorization.PermissionDenied");
    }

    [Fact]
    public async Task Command_ThatDoesNotOptIn_IsNotStepUpChecked()
    {
        WithPrincipal();
        var inner = new Mock<ICommandHandler<UngatedStepUpCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<UngatedStepUpCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new AuthorizationCommandDecorator<UngatedStepUpCommand, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new UngatedStepUpCommand());

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Query_WithTheMultiFactorClaim_ReachesTheHandler()
    {
        WithPrincipal(new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodRecoveryCode));
        var inner = new Mock<IQueryHandler<StepUpQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<StepUpQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new AuthorizationQueryDecorator<StepUpQuery, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new StepUpQuery());

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Query_WithoutTheMultiFactorClaim_IsForbidden()
    {
        // Enforced on queries as well as commands: a rule applied to one and not the other is exactly
        // the gap that opens when a use case moves from one side to the other.
        WithPrincipal();
        var inner = new Mock<IQueryHandler<StepUpQuery, Result>>();

        var sut = new AuthorizationQueryDecorator<StepUpQuery, Result>(
            inner.Object, _currentUser.Object, _permissionRegistry.Object);

        var result = await sut.HandleAsync(new StepUpQuery());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Authorization.MultiFactorRequired");
        inner.Verify(x => x.HandleAsync(It.IsAny<StepUpQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Command_WhenThePrincipalIsNull_IsForbidden()
    {
        // A hand-written ICurrentUserService double that populates only Role is common; absence must
        // deny rather than throw.
        _currentUser.Setup(x => x.User).Returns((ClaimsPrincipal)null!);
        var inner = InnerCommand();

        var result = await CreateCommandSut(inner).HandleAsync(new StepUpCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Authorization.MultiFactorRequired");
    }

    private void WithPrincipal(params Claim[] claims) =>
        _currentUser.Setup(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));

    private static Mock<ICommandHandler<StepUpCommand, Result>> InnerCommand()
    {
        var inner = new Mock<ICommandHandler<StepUpCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<StepUpCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        return inner;
    }

    private AuthorizationCommandDecorator<StepUpCommand, Result> CreateCommandSut(
        Mock<ICommandHandler<StepUpCommand, Result>> inner) =>
        new(inner.Object, _currentUser.Object, _permissionRegistry.Object);
}

/// <summary>
/// A command that opts into BOTH gates. Public, not nested: Moq proxies
/// <c>ICommandHandler&lt;StepUpCommand, Result&gt;</c> over it, and Castle cannot see a private type.
/// </summary>
public sealed record StepUpCommand : IRequiresPermission, IRequiresMfa
{
    /// <inheritdoc />
    public string Permission => "billing:rotate";
}

/// <summary>A query that opts into the step-up gate alone.</summary>
public sealed record StepUpQuery : IRequiresMfa;

/// <summary>A request that opts into neither gate.</summary>
public sealed record UngatedStepUpCommand;
