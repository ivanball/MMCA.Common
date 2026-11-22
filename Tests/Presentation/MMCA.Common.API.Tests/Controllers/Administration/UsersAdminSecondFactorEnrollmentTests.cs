using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Tests.Controllers.TwoFactor;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// A role listed under <c>Authorization:MultiFactor:RequiredForRoles</c> is given only to an account
/// that has turned on two-factor authentication: with a two-factor store registered, assigning it to
/// an account with no state, or with the factor off, is refused with 409 and
/// <c>UserAdministration.SecondFactorNotEnrolled</c> and nothing is saved. An enrolled target, an empty
/// list, a host with no store and a role that is not listed keep today's answer, and the privilege
/// ceiling is checked first.
/// </summary>
public sealed class UsersAdminSecondFactorEnrollmentTests
{
    private const UserIdentifierType TargetUserId = 4242;
    private const UserIdentifierType ActorUserId = 7171;
    private const string NotEnrolledCode = "UserAdministration.SecondFactorNotEnrolled";
    private const string CeilingCode = "UserAdministration.PrivilegeCeiling";
    private const string Admin = "Admin";
    private const string Support = "Support";
    private const string Member = "Member";

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly Mock<ITwoFactorStore> _store = new();

    public UsersAdminSecondFactorEnrollmentTests() =>
        _users
            .Setup(x => x.SetRolesAsync(It.IsAny<UserIdentifierType>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

    public static TheoryData<bool> NotEnrolledStates => new() { true, false };

    // -- Refused --
    [Theory]
    [MemberData(nameof(NotEnrolledStates))]
    public async Task ListedRole_TargetNotEnrolled_IsRefusedWith409AndNothingIsSaved(bool hasState)
    {
        ArrangeTargetState(hasState ? new TwoFactorState(IsTwoFactorEnabled: false, "pending", []) : null);
        var sut = CreateController(listed: [Admin], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        AssertNotEnrolledRefusal(result);
        VerifyNotSaved();
        VerifyStoreAskedOnlyForTheTarget();
    }

    [Fact]
    public async Task ListedRole_CallerEnrolledButTargetNot_IsRefused()
    {
        // The caller's own enrollment must not stand in for the target's.
        ArrangeTargetState(null);
        _store
            .Setup(x => x.GetAsync(ActorUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwoFactorState(IsTwoFactorEnabled: true, "secret", ["h1"]));
        var sut = CreateController(listed: [Admin], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        AssertNotEnrolledRefusal(result);
        VerifyNotSaved();
        VerifyStoreAskedOnlyForTheTarget();
    }

    [Fact]
    public async Task ListedRole_IsMatchedCaseInsensitively()
    {
        ArrangeTargetState(null);
        var sut = CreateController(listed: [Admin], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest(["admin"]));

        AssertNotEnrolledRefusal(result);
        VerifyNotSaved();
        VerifyStoreAskedOnlyForTheTarget();
    }

    // -- Allowed --
    [Fact]
    public async Task ListedRole_TargetEnrolled_IsSaved()
    {
        ArrangeTargetState(new TwoFactorState(IsTwoFactorEnabled: true, "secret", ["h1"]));
        var sut = CreateController(listed: [Admin], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        result.Should().BeOfType<NoContentResult>();
        VerifySavedOnce(Admin);
        VerifyStoreAskedOnlyForTheTarget();
    }

    [Fact]
    public async Task EmptyList_TargetNotEnrolled_IsSaved()
    {
        ArrangeTargetState(null);
        var sut = CreateController(listed: [], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        result.Should().BeOfType<NoContentResult>();
        VerifySavedOnce(Admin);
    }

    [Fact]
    public async Task NoTwoFactorStore_ListedRole_IsSaved()
    {
        var sut = CreateController(listed: [Admin], withStore: false);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        result.Should().BeOfType<NoContentResult>();
        VerifySavedOnce(Admin);
    }

    [Fact]
    public async Task RoleThatIsNotListed_TargetNotEnrolled_IsSaved()
    {
        ArrangeTargetState(null);
        var sut = CreateController(listed: [Admin], withStore: true);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Member]));

        result.Should().BeOfType<NoContentResult>();
        VerifySavedOnce(Member);
    }

    // -- Ordering --
    [Fact]
    public async Task CallerFailingThePrivilegeCeiling_GetsTheCeilingsAnswer_NotTheEnrollmentOne()
    {
        _users
            .Setup(x => x.GetRoleAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Member));
        ArrangeTargetState(null);
        var sut = CreateController(listed: [Admin], withStore: true, registry: CompiledRegistry(), callerRole: Support);

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest([Admin]));

        var refusal = result.Should().BeAssignableTo<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ErrorsJson(refusal).Should().Contain(CeilingCode).And.NotContain(NotEnrolledCode);
        VerifyNotSaved();
    }

    // -- Text --
    [Theory]
    [InlineData("en-US", "The account must turn on two-factor authentication before it can be given this role.")]
    [InlineData("es", "La cuenta debe activar la autenticación en dos pasos antes de que se le pueda asignar este rol.")]
    public void NotEnrolledCode_HasEnglishAndSpanishText(string culture, string expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorLocalization();
        using var provider = services.BuildServiceProvider();
        var localizer = provider.GetRequiredService<IErrorLocalizer>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            localizer.Localize(NotEnrolledCode, "fallback").Should().Be(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // -- Helpers --
    private static PermissionRegistry CompiledRegistry() => new(new Dictionary<string, IReadOnlySet<string>>
    {
        [Admin] = new HashSet<string>(StringComparer.Ordinal) { AdministrationPermissions.ManageUsers, AdministrationPermissions.ManageRoles },
        [Support] = new HashSet<string>(StringComparer.Ordinal) { AdministrationPermissions.ManageUsers },
        [Member] = new HashSet<string>(StringComparer.Ordinal),
    });

    private static string ErrorsJson(ObjectResult result)
    {
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Which;
        return JsonSerializer.Serialize(problem.Extensions["errors"]);
    }

    private static void AssertNotEnrolledRefusal(ActionResult result)
    {
        var refusal = result.Should().BeAssignableTo<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        ErrorsJson(refusal).Should().Contain(NotEnrolledCode);
    }

    private void ArrangeTargetState(ITwoFactorUserState? state) =>
        _store
            .Setup(x => x.GetAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

    // The enrollment that counts is the target account's: one read, for the target, never for anyone else.
    private void VerifyStoreAskedOnlyForTheTarget()
    {
        _store.Verify(x => x.GetAsync(TargetUserId, It.IsAny<CancellationToken>()), Times.Once());
        _store.Verify(
            x => x.GetAsync(It.Is<UserIdentifierType>(id => id != TargetUserId), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    private void VerifyNotSaved() =>
        _users.Verify(
            x => x.SetRolesAsync(It.IsAny<UserIdentifierType>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private void VerifySavedOnce(string role) =>
        _users.Verify(
            x => x.SetRolesAsync(
                TargetUserId,
                It.Is<IReadOnlyList<string>>(r => r.Count == 1 && r[0] == role),
                It.IsAny<CancellationToken>()),
            Times.Once);

    private TestUsersAdminController CreateController(
        string[] listed,
        bool withStore,
        IPermissionRegistry? registry = null,
        string callerRole = Admin)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MultiFactorAuthorizationSettings>(settings =>
        {
            foreach (var role in listed)
            {
                settings.RequiredForRoles.Add(role);
            }
        });
        if (withStore)
        {
            services.AddSingleton(_store.Object);
        }

        if (registry is not null)
        {
            services.AddSingleton(registry);
        }

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, ActorUserId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, callerRole),
                new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp),
            ],
            "Bearer"));

        return new TestUsersAdminController(_users.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = caller, RequestServices = services.BuildServiceProvider() },
            },
        };
    }
}
