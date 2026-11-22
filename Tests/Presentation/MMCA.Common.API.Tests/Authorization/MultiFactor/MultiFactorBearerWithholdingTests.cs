using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Auth;
using static MMCA.Common.API.Tests.Authorization.MultiFactor.MultiFactorBearerHost;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>
/// The role-level second factor at bearer token validation, over the framework's own JWT bearer
/// registrations and real signed tokens: with the role list set, a session without the <c>mfa</c>
/// claim goes without every listed role (in every claim spelling) and without the permission claims
/// that came with it, so every role and permission reader, the owner-or-admin bypass and the
/// background-work origin see an account without the role; a permission endpoint answers the coded
/// <c>403</c> and a plain <c>[Authorize]</c> endpoint answers <c>200</c>. A session with the factor, a
/// role that is not listed, a machine client, a service token and an empty list keep the token's
/// claims as they are.
/// </summary>
public sealed class MultiFactorBearerWithholdingTests
{
    private static readonly string[] AdminPermissions = [OrdersManage, UsersManage, ReportsRead];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- Withheld: a listed role without the factor --
    [Theory]
    [InlineData(BearerRegistration.Common)]
    [InlineData(BearerRegistration.Forwarded)]
    public async Task ListedRoleWithoutTheFactor_GoesWithoutTheRoleAndItsPermissions_AndCarriesTheMarkers(BearerRegistration registration)
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], registration));
        var token = host.CreateToken([.. AdminToken(), new Claim(AuthClaimTypes.Permission, DirectGrant)]);

        var report = await host.ReportAsync(token);

        report.IsInRoleAdmin.Should().BeFalse();
        report.HasRoleAdmin.Should().BeFalse();
        report.RoleValues.Should().BeEmpty();
        report.PermissionClaimsHeld.Should().BeEmpty("every permission claim is taken, including one no role grants");
        report.ValuesOf(AuthClaimTypes.Permission).Should().BeEmpty();
        report.CurrentUserRoles.Should().BeEmpty();
        report.CurrentUserIsInRoleAdmin.Should().BeFalse();
        report.WithheldRoles.Should().Equal(Admin);
        report.HasWithheldRole.Should().BeTrue();
        report.ValuesOf(AuthClaimTypes.MultiFactorWithheldRole).Should().Equal(Admin);
        report.ValuesOf(AuthClaimTypes.MultiFactorWithheldPermission).Should().BeEquivalentTo([.. AdminPermissions, DirectGrant]);
        report.ValuesOf("sub").Concat(report.ValuesOf(ClaimTypes.NameIdentifier)).Should().Contain("7", "the subject is untouched");
        report.ValuesOf(OwnerClaim).Should().Equal("42");
    }

    [Theory]
    [InlineData(BearerRegistration.Common)]
    [InlineData(BearerRegistration.Forwarded)]
    public async Task ListedRoleWithoutTheFactor_PermissionEndpointAnswersTheCoded403_AndAPlainAuthorizeEndpointAnswers200(BearerRegistration registration)
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], registration));
        using var client = host.Bearer(host.CreateToken(AdminToken()));

        using var orders = await client.GetAsync(new Uri(OrdersPath, UriKind.Relative), Ct);
        using var plain = await client.GetAsync(new Uri(ReportPath, UriKind.Relative), Ct);

        orders.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(orders)).Should().Be(AuthErrorCodes.MultiFactorRequired);
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListedRoleWithoutTheFactor_IsWithheldInEverySpelling_IncludingTheIdentitysOwnRoleClaimType()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], RawClaims: true, RoleClaimType: "app_role"));
        var token = host.CreateToken(
            Subject(),
            new Claim(ClaimTypes.Role, Admin),
            new Claim("role", "admin"),
            new Claim("roles", "ADMIN"),
            new Claim("app_role", Admin),
            new Claim("role", Member));

        var report = await host.ReportAsync(token);

        report.IsInRoleAdmin.Should().BeFalse("the identity's own role claim type (app_role) is withheld too");
        report.HasRoleAdmin.Should().BeFalse();
        report.ValuesOf("app_role").Should().BeEmpty();
        report.ValuesOf(ClaimTypes.Role).Should().BeEmpty();
        report.ValuesOf("roles").Should().BeEmpty();
        report.ValuesOf("role").Should().Equal(Member);
        report.RoleValues.Should().Equal(Member);
        report.WithheldRoles.Should().ContainSingle().Which.Should().BeEquivalentTo(Admin);
    }

    [Fact]
    public async Task ListedRoleAndAnotherRole_KeepsTheOtherRoleAndThePermissionsTheRegistryGrantsIt()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin]));
        var token = host.CreateToken([.. AdminToken(), new Claim(ClaimTypes.Role, Member)]);
        using var client = host.Bearer(token);

        var report = await host.ReportAsync(token);
        using var reports = await client.GetAsync(new Uri(ReportsPath, UriKind.Relative), Ct);
        using var orders = await client.GetAsync(new Uri(OrdersPath, UriKind.Relative), Ct);

        report.RoleValues.Should().Equal(Member);
        report.HasRoleAdmin.Should().BeFalse();
        report.PermissionClaimsHeld.Should().Equal(ReportsRead);
        report.CurrentUserRoles.Should().Equal(Member);
        report.CurrentUserPermissions.Should().Equal(ReportsRead);
        report.WithheldRoles.Should().Equal(Admin);
        report.ValuesOf(AuthClaimTypes.MultiFactorWithheldPermission).Should().BeEquivalentTo(
            [OrdersManage, UsersManage],
            "a permission the remaining role is granted is kept, not withheld");
        reports.StatusCode.Should().Be(HttpStatusCode.OK, "the permission the remaining role grants still opens its endpoint");
        orders.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(orders)).Should().Be(AuthErrorCodes.MultiFactorRequired);
    }

    [Fact]
    public async Task OwnerOrAdminFilter_TreatsTheWithheldCallerAsANonAdmin()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin]));
        using var withheld = host.Bearer(host.CreateToken(AdminToken()));
        using var stepped = host.Bearer(host.CreateToken([.. AdminToken(), Factor(AuthClaimTypes.MultiFactorMethodTotp)]));

        using var own = await withheld.GetAsync(new Uri(CustomersPath + "/42", UriKind.Relative), Ct);
        using var others = await withheld.GetAsync(new Uri(CustomersPath + "/99", UriKind.Relative), Ct);
        using var othersWithTheFactor = await stepped.GetAsync(new Uri(CustomersPath + "/99", UriKind.Relative), Ct);

        own.StatusCode.Should().Be(HttpStatusCode.OK, "the account's own resource stays reachable");
        others.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the bypass role is withheld, so another owner's resource is refused");
        othersWithTheFactor.StatusCode.Should().Be(HttpStatusCode.OK, "the same admin with the factor keeps the bypass");
    }

    [Fact]
    public async Task HubAccessTokenPath_GetsTheSameWithholdingAsTheHeaderPath()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin]));
        var token = host.CreateToken([.. AdminToken(), new Claim(ClaimTypes.Role, Member)]);

        var header = await host.ReportAsync(token);
        var hub = await host.HubReportAsync(token);

        hub.HasWithheldRole.Should().BeTrue();
        hub.RoleValues.Should().Equal(Member);
        hub.Claims.Should().Equal(header.Claims);
    }

    [Fact]
    public async Task AddAuthorizationPoliciesCalledTwice_WithholdsOnce_WithNoDuplicatedMarkers()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], RegisterPoliciesTwice: true));

        var report = await host.ReportAsync(host.CreateToken(AdminToken()));

        report.ValuesOf(AuthClaimTypes.MultiFactorWithheldRole).Should().Equal(Admin);
        report.ValuesOf(AuthClaimTypes.MultiFactorWithheldPermission).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(AdminPermissions);
    }

    [Fact]
    public async Task HostTokenValidatedHandler_StillRuns_AndARoleItAddsIsWithheldAfterIt()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], HostTokenValidated: true));

        var report = await host.ReportAsync(host.CreateToken(Subject(), new Claim(ClaimTypes.Role, Member)));

        report.ValuesOf(HostHookClaim).Should().Equal("ran");
        report.RoleValues.Should().Equal(Member);
        report.WithheldRoles.Should().Equal(Admin);
    }

    [Fact]
    public async Task BackgroundWorkOrigin_FromAWithheldPrincipal_CarriesNoListedRoleOrWithheldPermission_AndFromTheFactorCarriesThem()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin]));

        var withheld = await host.ReportAsync(host.CreateToken(AdminToken()));
        var stepped = await host.ReportAsync(host.CreateToken([.. AdminToken(), Factor(AuthClaimTypes.MultiFactorMethodTotp)]));

        // The origin capture flattens ICurrentUserService.Roles and the permission claims of
        // ICurrentUserService.User (InternalCommandOriginCapture, the outbox and broker hops).
        withheld.CurrentUserRoles.Should().BeEmpty();
        withheld.CurrentUserPermissions.Should().BeEmpty();
        stepped.CurrentUserRoles.Should().Equal(Admin);
        stepped.CurrentUserPermissions.Should().BeEquivalentTo(AdminPermissions);
    }

    // -- Not affected --
    [Theory]
    [InlineData(AuthClaimTypes.MultiFactorMethodTotp)]
    [InlineData(AuthClaimTypes.MultiFactorMethodPasskey)]
    public async Task ListedRoleWithTheFactor_KeepsTheTokensClaims_AndOpensThePermissionEndpoint(string method)
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], RawClaims: true));
        var token = host.CreateToken([.. AdminToken(), Factor(method)]);
        using var client = host.Bearer(token);

        var report = await host.ReportAsync(token);
        using var orders = await client.GetAsync(new Uri(OrdersPath, UriKind.Relative), Ct);

        report.Claims.Should().Equal(TokenClaims(token));
        report.HasWithheldRole.Should().BeFalse();
        orders.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RoleThatIsNotListed_KeepsTheTokensClaims()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], RawClaims: true));
        var token = host.CreateToken(Subject(), new Claim(ClaimTypes.Role, Member), new Claim(AuthClaimTypes.Permission, ReportsRead));

        var report = await host.ReportAsync(token);

        report.Claims.Should().Equal(TokenClaims(token));
        report.PermissionClaimsHeld.Should().Equal(ReportsRead);
    }

    [Fact]
    public async Task MachineClient_InAListedRole_KeepsTheTokensClaims()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin], RawClaims: true));
        var token = host.CreateToken([.. AdminToken(), new Claim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine)]);
        using var client = host.Bearer(token);

        var report = await host.ReportAsync(token);
        using var orders = await client.GetAsync(new Uri(OrdersPath, UriKind.Relative), Ct);

        report.Claims.Should().Equal(TokenClaims(token));
        orders.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ServiceToken_KeepsTheTokensClaims()
    {
        await using var host = await StartAsync(new BearerHostOptions([Admin]));
        var token = CreateServiceToken(new Claim("sub", "orders-service"), new Claim("svc", "orders-service"));

        var report = await host.ReportAsync(token, ServiceReportPath);

        report.Claims.Should().Equal(TokenClaims(token));
        report.HasWithheldRole.Should().BeFalse();
    }

    [Theory]
    [InlineData(BearerRegistration.Common)]
    [InlineData(BearerRegistration.Forwarded)]
    public async Task EmptyList_ClaimsAreIdenticalToTheTokens(BearerRegistration registration)
    {
        await using var host = await StartAsync(new BearerHostOptions([], registration, RawClaims: true));
        var token = host.CreateToken([.. AdminToken(), new Claim(ClaimTypes.Role, Member)]);
        using var client = host.Bearer(token);

        var header = await host.ReportAsync(token);
        var hub = await host.HubReportAsync(token);
        using var orders = await client.GetAsync(new Uri(OrdersPath, UriKind.Relative), Ct);

        header.Claims.Should().Equal(TokenClaims(token));
        hub.Claims.Should().Equal(TokenClaims(token));
        orders.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -- Helpers --
    private static Claim Subject() => new("sub", "7");

    private static Claim Factor(string method) => new(AuthClaimTypes.MultiFactor, method);

    // The shape TokenService issues for an Admin: subject, the role and one permission claim per
    // permission the registry grants the role, plus the owner claim the owner-or-admin filter reads.
    private static Claim[] AdminToken() =>
    [
        Subject(),
        new Claim(OwnerClaim, "42"),
        new Claim(ClaimTypes.Role, Admin),
        .. AdminPermissions.Select(permission => new Claim(AuthClaimTypes.Permission, permission)),
    ];

    private static Task<MultiFactorBearerHost> StartAsync(BearerHostOptions options) => MultiFactorBearerHost.StartAsync(options);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString();
    }
}
