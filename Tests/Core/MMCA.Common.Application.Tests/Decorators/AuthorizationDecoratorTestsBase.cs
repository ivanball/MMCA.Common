using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The authorization decorator contract shared by the command and the query pipeline (both run
/// <c>AuthorizationGate</c>): an unguarded request is never checked, a granted permission delegates,
/// a denied one returns Forbidden without reaching the handler, a permission claim grants on its own
/// but never bypasses the check, and a non-Result handler still resolves. Each subclass supplies the
/// decorator under test and its request types.
/// </summary>
/// <typeparam name="TResult">The result type the subclass's handlers return.</typeparam>
public abstract class AuthorizationDecoratorTestsBase<TResult>
    where TResult : Result
{
    protected AuthorizationDecoratorTestsBase() =>
        CurrentUser.Setup(x => x.Roles).Returns(["Member"]);

    /// <summary>Which request type the subclass builds the decorator for.</summary>
    protected enum RequestKind
    {
        /// <summary>A request without the permission marker.</summary>
        Unguarded,

        /// <summary>A request requiring <see cref="GuardedPermission"/>.</summary>
        Guarded,
    }

    /// <summary>The current user every decorator under test reads.</summary>
    protected Mock<ICurrentUserService> CurrentUser { get; } = new();

    /// <summary>The permission registry every decorator under test asks.</summary>
    protected Mock<IPermissionRegistry> PermissionRegistry { get; } = new();

    /// <summary>The permission the guarded request requires.</summary>
    protected abstract string GuardedPermission { get; }

    /// <summary>A permission other than <see cref="GuardedPermission"/>, held as a claim to prove it does not grant.</summary>
    protected abstract string UnrelatedPermission { get; }

    /// <summary>The type name of the guarded request, reported as the denial's source.</summary>
    protected abstract string GuardedRequestName { get; }

    /// <summary>The successful result the inner handler returns.</summary>
    protected abstract TResult SuccessResult { get; }

    /// <summary>
    /// Builds the decorator over an inner handler that answers through <paramref name="inner"/> and
    /// the shared <see cref="CurrentUser"/> and <see cref="PermissionRegistry"/>, and returns a call
    /// that sends it one request of <paramref name="kind"/>.
    /// </summary>
    /// <typeparam name="T">The handler's result type (a non-Result type exercises the lazy factory).</typeparam>
    protected abstract Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner);

    /// <summary>Extra checks on a successful pass-through (the query also checks the value).</summary>
    protected virtual void AssertPassedThrough(TResult result) => result.IsSuccess.Should().BeTrue();

    // ── A request without the marker is never checked at all ──
    [Fact]
    public async Task HandleAsync_RequestWithoutPermission_DelegatesToInner()
    {
        var calls = 0;
        var handle = CreateSut(
            RequestKind.Unguarded,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        var result = await handle();

        AssertPassedThrough(result);
        calls.Should().Be(1);
        PermissionRegistry.Verify(
            x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Granted permission passes through ──
    [Fact]
    public async Task HandleAsync_WhenPermissionGranted_DelegatesToInner()
    {
        var calls = 0;
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), GuardedPermission))
            .Returns(true);
        var handle = CreateSut(
            RequestKind.Guarded,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        var result = await handle();

        result.IsSuccess.Should().BeTrue();
        calls.Should().Be(1);
    }

    // ── Denied permission short-circuits with Forbidden and never reaches the handler ──
    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_ReturnsForbiddenWithoutCallingInner()
    {
        var calls = 0;
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);
        var handle = CreateSut(
            RequestKind.Guarded,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        var result = await handle();

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Authorization.PermissionDenied");
        error.Type.Should().Be(ErrorType.Forbidden);
        error.Source.Should().Be(GuardedRequestName);
        error.Message.Should().Contain(GuardedPermission);
        calls.Should().Be(0);
    }

    // ── A permission claim grants on its own, with no role behind it: that is how a grant stored in
    // the minting host reaches a service that does not own the grant table ──
    [Fact]
    public async Task HandleAsync_WhenOnlyThePermissionClaimGrantsIt_DelegatesToInner()
    {
        var calls = 0;
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);
        WithPermissionClaims(GuardedPermission);
        var handle = CreateSut(
            RequestKind.Guarded,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        var result = await handle();

        result.IsSuccess.Should().BeTrue();
        calls.Should().Be(1);
    }

    // ── Neither source grants it: the claim is additive, never a way around the check ──
    [Fact]
    public async Task HandleAsync_WhenNeitherRoleNorClaimGrantsIt_ReturnsForbidden()
    {
        var calls = 0;
        PermissionRegistry.Setup(x => x.HasPermission(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .Returns(false);
        WithPermissionClaims(UnrelatedPermission);
        var handle = CreateSut(
            RequestKind.Guarded,
            () =>
            {
                calls++;
                return Task.FromResult(SuccessResult);
            });

        var result = await handle();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Authorization.PermissionDenied");
        calls.Should().Be(0);
    }

    // Scrutor's TryDecorate is unconditional, so a handler whose TResult is neither Result nor
    // Result<T> gets decorated too. Building the failure delegate eagerly would turn that into a
    // TypeInitializationException at RESOLVE time, even for a request that is never denied.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnguardedRequest_PassesThrough()
    {
        var handle = CreateSut(RequestKind.Unguarded, () => Task.FromResult("handled"));

        var result = await handle();

        result.Should().Be("handled");
    }

    /// <summary>Gives the current user a principal holding one permission claim per entry.</summary>
    protected void WithPermissionClaims(params string[] permissions) =>
        CurrentUser.Setup(x => x.User).Returns(
            new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(permission => new Claim(AuthClaimTypes.Permission, permission)),
                "TestAuth")));
}
