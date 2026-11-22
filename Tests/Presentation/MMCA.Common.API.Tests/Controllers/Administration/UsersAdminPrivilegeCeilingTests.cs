using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// Pins security review 2026-11-21 item A2 at the user-administration controller: with a permission
/// registry registered, set-roles, lock and unlock are refused with 403 and
/// <c>UserAdministration.PrivilegeCeiling</c> when the requested role, or the target account's current
/// role, grants a permission the caller does not hold (a permission claim, a role the registry grants it
/// to, or a stored grant all count as holding it). The refusal names no permission and the service's
/// mutating member is never reached. A malformed role request, the self refusal and a missing target keep
/// their existing answers, a host with no registry applies no ceiling, and each success logs one line.
/// </summary>
public sealed class UsersAdminPrivilegeCeilingTests : IDisposable
{
    private const UserIdentifierType TargetUserId = 4242;
    private const UserIdentifierType ActorUserId = 7171;
    private const string CeilingCode = "UserAdministration.PrivilegeCeiling";

    // Admin holds both administration permissions; Support only manages users; Member holds nothing.
    private const string Admin = "Admin";
    private const string Support = "Support";
    private const string Member = "Member";

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly RecordingLoggerProvider _logs = new();

    public UsersAdminPrivilegeCeilingTests()
    {
        _users
            .Setup(x => x.SetRolesAsync(It.IsAny<UserIdentifierType>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _users
            .Setup(x => x.SetLockedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    public void Dispose() => _logs.Dispose();

    // -- Refused: the requested role --
    [Fact]
    public async Task SetRoles_RequestedRoleGrantsAPermissionTheCallerLacks_IsRefusedAndNeverReachesTheService()
    {
        ArrangeTargetRole(Member);
        var sut = CreateController(Caller(Role(Support)), CompiledRegistry());

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        AssertCeilingRefusal(result);
        VerifyNoMutation();
    }

    // -- Refused: the target account's current role --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task TargetsCurrentRoleGrantsAPermissionTheCallerLacks_IsRefusedAndNeverReachesTheService(string action)
    {
        ArrangeTargetRole(Admin);
        var sut = CreateController(Caller(Role(Support)), CompiledRegistry());

        ActionResult result = await InvokeAsync(sut, action, Member);

        AssertCeilingRefusal(result);
        VerifyNoMutation();
    }

    // -- Allowed: the caller holds every permission of both roles --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task CallerWhoseRoleGrantsEveryPermissionOfBothRoles_Succeeds(string action)
    {
        ArrangeTargetRole(Admin);
        var sut = CreateController(Caller(Role(Admin)), CompiledRegistry());

        ActionResult result = await InvokeAsync(sut, action, Admin);

        result.Should().BeOfType<NoContentResult>();
        VerifyMutatedOnce(action, Admin);
    }

    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task CallerHoldingTheMissingPermissionAsAClaim_Succeeds(string action)
    {
        ArrangeTargetRole(Admin);
        var sut = CreateController(
            Caller(Role(Support), new Claim(AuthClaimTypes.Permission, AdministrationPermissions.ManageRoles)),
            CompiledRegistry());

        ActionResult result = await InvokeAsync(sut, action, Admin);

        result.Should().BeOfType<NoContentResult>("a permission claim counts as holding it, as the permission handler counts it");
        VerifyMutatedOnce(action, Admin);
    }

    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task CallerHoldingTheMissingPermissionThroughAStoredGrant_Succeeds(string action)
    {
        ArrangeTargetRole(Admin);
        var grants = new Mock<IPermissionGrantCache>();
        grants.Setup(g => g.GetPermissions(It.IsAny<string>())).Returns(new HashSet<string>(StringComparer.Ordinal));
        grants
            .Setup(g => g.GetPermissions(Support))
            .Returns(new HashSet<string>(StringComparer.Ordinal) { AdministrationPermissions.ManageRoles });
        var registry = new LayeredPermissionRegistry(CompiledRegistry(), grants.Object);
        var sut = CreateController(Caller(Role(Support)), registry);

        ActionResult result = await InvokeAsync(sut, action, Admin);

        result.Should().BeOfType<NoContentResult>("a stored grant counts as holding it, as the permission handler counts it");
        VerifyMutatedOnce(action, Admin);
    }

    // -- A malformed role request is the service's 400, with no ceiling and no load ahead of it --
    [Theory]
    [InlineData("")]
    [InlineData("Admin,Member")]
    public async Task SetRoles_ZeroOrSeveralRoles_ReachesTheServiceUnchangedAndReturnsItsBadRequest(string roleList)
    {
        string[] roles = roleList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var exactlyOne = Error.Validation("App.ExactlyOneRole", "Exactly one role is required.", "SetRolesAsync", "Roles");
        _users
            .Setup(x => x.SetRolesAsync(TargetUserId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(exactlyOne));
        var registry = new Mock<IPermissionRegistry>();
        var sut = CreateController(Caller(Role(Member)), registry.Object);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest(roles));

        var problem = result.Should().BeAssignableTo<ObjectResult>().Which;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ErrorsJson(problem).Should().Contain("App.ExactlyOneRole").And.NotContain(CeilingCode);
        _users.Verify(
            x => x.SetRolesAsync(
                TargetUserId,
                It.Is<IReadOnlyList<string>>(r => r.SequenceEqual(roles)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _users.Verify(x => x.GetRoleAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.VerifyNoOtherCalls();
    }

    // -- The self refusal still comes first --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task OnTheCallersOwnAccount_TheSelfRefusalComesBeforeTheCeiling(string action)
    {
        ArrangeTargetRole(Admin);
        var sut = CreateController(Caller(TargetUserId, Role(Member)), CompiledRegistry());

        ActionResult result = await InvokeAsync(sut, action, Admin);

        var refusal = result.Should().BeAssignableTo<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ErrorsJson(refusal).Should().Contain("UserAdministration.Self").And.NotContain(CeilingCode);
        _users.Verify(x => x.GetRoleAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoMutation();
    }

    // -- A missing target keeps its not-found answer --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task TargetThatDoesNotExist_ReturnsTheSameNotFoundAnswerAsWithoutTheCeiling(string action)
    {
        // Before: no registry, the service itself reports the missing account.
        var beforeUsers = new Mock<IUserAdministrationService<TestUserDto>>();
        beforeUsers
            .Setup(x => x.SetRolesAsync(TargetUserId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFound));
        beforeUsers
            .Setup(x => x.SetLockedAsync(TargetUserId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFound));
        var before = CreateController(beforeUsers.Object, Caller(Role(Admin)), registry: null);
        var beforeResult = (await InvokeAsync(before, action, Support)).Should().BeAssignableTo<ObjectResult>().Which;

        // After: a registry is registered and the target lookup reports the missing account.
        _users
            .Setup(x => x.GetRoleAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(Error.NotFound));
        var after = CreateController(Caller(Role(Admin)), CompiledRegistry());
        var afterResult = (await InvokeAsync(after, action, Support)).Should().BeAssignableTo<ObjectResult>().Which;

        beforeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        afterResult.StatusCode.Should().Be(beforeResult.StatusCode);
        ErrorsJson(afterResult).Should().Contain(Error.NotFound.Code).And.NotContain(CeilingCode);
    }

    // -- A role-only host (no registry) applies no ceiling --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task WithNoPermissionRegistry_TheServiceIsCalledWithNoCeiling(string action)
    {
        var sut = CreateController(Caller(Role(Member)), registry: null);

        ActionResult result = await InvokeAsync(sut, action, Admin);

        result.Should().BeOfType<NoContentResult>();
        VerifyMutatedOnce(action, Admin);
        _users.Verify(x => x.GetRoleAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- One Information line per success --
    [Theory]
    [InlineData("set-roles")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task Success_LogsOneInformationLineNamingTheActionTheTargetAndTheActor(string action)
    {
        ArrangeTargetRole(Support);
        var sut = CreateController(Caller(Role(Admin)), CompiledRegistry());

        ActionResult result = await InvokeAsync(sut, action, Support);

        result.Should().BeOfType<NoContentResult>();
        var line = _logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which.Message;
        line.Should().Contain(TargetUserId.ToString(CultureInfo.InvariantCulture), "the line names the target account");
        line.Should().Contain(ActorUserId.ToString(CultureInfo.InvariantCulture), "the line names the acting administrator");
        switch (action)
        {
            case "set-roles":
                line.Should().ContainEquivalentOf("role");
                break;
            case "lock":
                line.Should().ContainEquivalentOf("lock").And.NotContainEquivalentOf("unlock");
                break;
            default:
                line.Should().ContainEquivalentOf("unlock");
                break;
        }
    }

    // -- Helpers --
    private static PermissionRegistry CompiledRegistry() => new(new Dictionary<string, IReadOnlySet<string>>
    {
        [Admin] = new HashSet<string>(StringComparer.Ordinal) { AdministrationPermissions.ManageUsers, AdministrationPermissions.ManageRoles },
        [Support] = new HashSet<string>(StringComparer.Ordinal) { AdministrationPermissions.ManageUsers },
        [Member] = new HashSet<string>(StringComparer.Ordinal),
    });

    private static Claim Role(string role) => new(ClaimTypes.Role, role);

    private static ClaimsPrincipal Caller(params Claim[] claims) => Caller(ActorUserId, claims);

    private static ClaimsPrincipal Caller(UserIdentifierType userId, params Claim[] claims) =>
        new(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture)), .. claims],
            "Bearer"));

    private static Task<ActionResult> InvokeAsync(TestUsersAdminController sut, string action, string requestedRole) =>
        action switch
        {
            "set-roles" => sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([requestedRole])),
            "lock" => sut.LockAsync(TargetUserId),
            "unlock" => sut.UnlockAsync(TargetUserId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    private static string ErrorsJson(ObjectResult result)
    {
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        return JsonSerializer.Serialize(problem.Extensions["errors"]);
    }

    private static void AssertCeilingRefusal(ActionResult result)
    {
        var refusal = result.Should().BeAssignableTo<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ErrorsJson(refusal).Should().Contain(CeilingCode);

        var whole = JsonSerializer.Serialize(refusal.Value);
        whole.Should().NotContain(AdministrationPermissions.ManageRoles, "the refusal names no permission");
        whole.Should().NotContain(AdministrationPermissions.ManageUsers, "the refusal names no permission");
    }

    private void ArrangeTargetRole(string role) =>
        _users
            .Setup(x => x.GetRoleAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(role));

    private void VerifyNoMutation()
    {
        _users.Verify(
            x => x.SetRolesAsync(It.IsAny<UserIdentifierType>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _users.Verify(
            x => x.SetLockedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void VerifyMutatedOnce(string action, string requestedRole)
    {
        switch (action)
        {
            case "set-roles":
                _users.Verify(
                    x => x.SetRolesAsync(
                        TargetUserId,
                        It.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == requestedRole),
                        It.IsAny<CancellationToken>()),
                    Times.Once);
                break;
            case "lock":
                _users.Verify(x => x.SetLockedAsync(TargetUserId, true, It.IsAny<CancellationToken>()), Times.Once);
                break;
            default:
                _users.Verify(x => x.SetLockedAsync(TargetUserId, false, It.IsAny<CancellationToken>()), Times.Once);
                break;
        }
    }

    private TestUsersAdminController CreateController(ClaimsPrincipal user, IPermissionRegistry? registry) =>
        CreateController(_users.Object, user, registry);

    private TestUsersAdminController CreateController(
        IUserAdministrationService<TestUserDto> users,
        ClaimsPrincipal user,
        IPermissionRegistry? registry)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        if (registry is not null)
        {
            services.AddSingleton(registry);
        }

        var httpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = services.BuildServiceProvider(),
        };

        return new TestUsersAdminController(users)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
