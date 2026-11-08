using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.MachineClients;

namespace MMCA.Common.API.Tests.Controllers.MachineClients;

/// <summary>
/// The client credentials grant end to end (ADR-140, spec D): <c>POST auth/token</c> through real
/// routing, the framework's service and token signer, JWT bearer validation and the soft-deleted-user
/// middleware. Clients are created, rotated, promoted and revoked through the administration endpoints
/// an operator calls.
/// </summary>
public sealed class MachineTokenEndpointTests
{
    // Spec D: a valid secret returns a token whose sub is the service account, with the role's
    // permissions and token_use=machine.
    [Fact]
    public async Task AValidSecret_ReturnsATokenForTheServiceAccount_WithTheRolePermissions_AndTokenUseMachine()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("expires_in").GetInt32().Should().Be(600, "MachineClients:AccessTokenMinutes is 10 in this host");
        var claims = ReadClaims(body.GetProperty("access_token").GetString()!);
        claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.Subject)
            .Which.Value.Should().Be(created.Client.ServiceAccountUserId.ToString(CultureInfo.InvariantCulture));
        claims.Where(c => c.Type == AuthClaimTypes.Permission).Select(c => c.Value)
            .Should().BeEquivalentTo(MachineClientTestHost.OrdersRead, MachineClientTestHost.OrdersWrite);
        claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.TokenUse).Which.Value.Should().Be(AuthClaimTypes.TokenUseMachine);
        claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.ClientId).Which.Value.Should().Be(created.Client.ClientId.ToString());
    }

    [Fact]
    public async Task AMachineToken_HasNoRefreshToken_NoSession_NoAuthTime_AndNoMfa()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret);

        var body = await ReadJsonAsync(response);
        body.TryGetProperty("refresh_token", out _).Should().BeFalse("a program holds its secret and asks again");
        var claims = ReadClaims(body.GetProperty("access_token").GetString()!);
        claims.Should().NotContain(c => c.Type == AuthClaimTypes.AuthTime, "no interactive sign-in stands behind a machine token");
        claims.Should().NotContain(c => c.Type == AuthClaimTypes.SessionId, "no refresh session is opened");
        claims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor, "IRequiresMfa use cases must refuse a machine");
    }

    [Fact]
    public async Task AMachineToken_IsAcceptedByAnAuthenticatedEndpoint_AndRecordsTheUse()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);
        var token = await ObtainTokenAsync(host, created.Client.ClientId, created.ClientSecret);

        using var probe = await host.BearerClient(token).GetAsync(new Uri(MachineClientTestHost.ProbePath, UriKind.Relative), Ct);

        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.Rows.Single().LastUsedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task HttpBasicClientAuthentication_IsAccepted()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret, useBasic: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Spec D: a wrong secret returns invalid_client.
    [Fact]
    public async Task AWrongSecret_ReturnsInvalidClient()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret + "x");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(MachineTokenErrorResponse.InvalidClient);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnknownClient_ReturnsTheSameInvalidClient()
    {
        await using var host = await MachineClientTestHost.StartAsync();

        using var response = await host.RequestTokenAsync(Guid.NewGuid().ToString(), "anything");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(MachineTokenErrorResponse.InvalidClient);
    }

    [Fact]
    public async Task AnotherGrantType_ReturnsUnsupportedGrantType()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret, grantType: "password");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(MachineTokenErrorResponse.UnsupportedGrantType);
    }

    // Spec D: a revoked client's outstanding token is rejected by the middleware.
    [Fact]
    public async Task ARevokedClientsOutstandingToken_IsRejectedBySoftDeletedUserMiddleware()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);
        var token = await ObtainTokenAsync(host, created.Client.ClientId, created.ClientSecret);
        using (var before = await host.BearerClient(token).GetAsync(new Uri(MachineClientTestHost.ProbePath, UriKind.Relative), Ct))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK, "the token is valid before the revoke");
        }

        using var revoke = await host.OperatorClient().PostAsync(
            new Uri($"{MachineClientTestHost.AdminPath}/{created.Client.ClientId}/revoke", UriKind.Relative), null, Ct);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        using var after = await host.BearerClient(token).GetAsync(new Uri(MachineClientTestHost.ProbePath, UriKind.Relative), Ct);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the still-unexpired token must die with the client");
        using var again = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret);
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a revoked client gets no new token either");
    }

    // Spec D: a role with users:impersonate (users:view-as here) cannot back a client; users:manage neither.
    [Theory]
    [InlineData(MachineClientTestHost.SupportRole)]
    [InlineData(MachineClientTestHost.UserAdminRole)]
    public async Task ARoleThatCanViewAsOrManageUsers_CannotBackAClient(string role)
    {
        await using var host = await MachineClientTestHost.StartAsync();

        using var response = await host.OperatorClient().PostAsJsonAsync(
            new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), new CreateMachineClientRequest("Nightly sync", role), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("MachineClients.RoleNotAllowed");
        host.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AClientWhoseServiceAccountGainedViewAs_IsRefusedAtMint()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);
        host.Accounts.ChangeRole(created.Client.ServiceAccountUserId, MachineClientTestHost.SupportRole);

        using var response = await host.RequestTokenAsync(created.Client.ClientId.ToString(), created.ClientSecret);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(MachineTokenErrorResponse.InvalidClient);
    }

    // Spec D: the secondary secret works before promotion and the old one fails after it.
    [Fact]
    public async Task TheSecondarySecret_WorksBeforePromotion_AndTheOldPrimaryFailsAfterIt()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);
        var clientId = created.Client.ClientId.ToString();
        var operatorClient = host.OperatorClient();

        using var rotate = await operatorClient.PostAsync(new Uri($"{MachineClientTestHost.AdminPath}/{clientId}/rotate", UriKind.Relative), null, Ct);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondary = (await rotate.Content.ReadFromJsonAsync<MachineClientSecretResponse>(Ct))!.ClientSecret;
        secondary.Should().NotBe(created.ClientSecret);

        using (var bySecondary = await host.RequestTokenAsync(clientId, secondary))
        {
            bySecondary.StatusCode.Should().Be(HttpStatusCode.OK, "the rotated secret works before promotion");
        }

        using (var byPrimary = await host.RequestTokenAsync(clientId, created.ClientSecret))
        {
            byPrimary.StatusCode.Should().Be(HttpStatusCode.OK, "the old secret keeps working until promotion");
        }

        using var promote = await operatorClient.PostAsync(new Uri($"{MachineClientTestHost.AdminPath}/{clientId}/promote", UriKind.Relative), null, Ct);
        promote.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var oldPrimary = await host.RequestTokenAsync(clientId, created.ClientSecret))
        {
            oldPrimary.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "promotion retires the old primary");
        }

        using var promoted = await host.RequestTokenAsync(clientId, secondary);
        promoted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheList_ShowsNoSecret()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);

        using var response = await host.OperatorClient().GetAsync(new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().Contain(created.Client.ClientId.ToString()).And.NotContain(created.ClientSecret);
    }

    [Fact]
    public async Task ACallerWithoutClientsManage_CannotAdministerClients()
    {
        await using var host = await MachineClientTestHost.StartAsync();
        var created = await CreateClientAsync(host, MachineClientTestHost.IntegrationRole);
        var machineToken = await ObtainTokenAsync(host, created.Client.ClientId, created.ClientSecret);

        using var response = await host.BearerClient(machineToken).GetAsync(new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Disabled means every route 404s (the table half is MachineClientModelGateTests).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disabled_EveryRouteAnswers404(bool addMachineClients)
    {
        await using var host = await MachineClientTestHost.StartAsync(enabled: false, addMachineClients);
        var operatorClient = host.OperatorClient();
        var id = Guid.NewGuid();

        using var token = await host.RequestTokenAsync(id.ToString(), "secret");
        using var list = await operatorClient.GetAsync(new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), Ct);
        using var create = await operatorClient.PostAsJsonAsync(
            new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), new CreateMachineClientRequest("Nightly sync", MachineClientTestHost.IntegrationRole), Ct);
        using var rotate = await operatorClient.PostAsync(new Uri($"{MachineClientTestHost.AdminPath}/{id}/rotate", UriKind.Relative), null, Ct);
        using var promote = await operatorClient.PostAsync(new Uri($"{MachineClientTestHost.AdminPath}/{id}/promote", UriKind.Relative), null, Ct);
        using var revoke = await operatorClient.PostAsync(new Uri($"{MachineClientTestHost.AdminPath}/{id}/revoke", UriKind.Relative), null, Ct);

        new[] { token, list, create, rotate, promote, revoke }.Select(r => r.StatusCode)
            .Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        host.Store.Rows.Should().BeEmpty();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static async Task<MachineClientSecretResponse> CreateClientAsync(MachineClientTestHost host, string role)
    {
        using var response = await host.OperatorClient().PostAsJsonAsync(
            new Uri(MachineClientTestHost.AdminPath, UriKind.Relative), new CreateMachineClientRequest("Nightly sync", role), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MachineClientSecretResponse>(Ct))!;
    }

    private static async Task<string> ObtainTokenAsync(MachineClientTestHost host, Guid clientId, string secret)
    {
        using var response = await host.RequestTokenAsync(clientId.ToString(), secret);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadJsonAsync(response)).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<System.Security.Claims.Claim> ReadClaims(string token) =>
        [.. new JwtSecurityTokenHandler().ReadJwtToken(token).Claims];
}
