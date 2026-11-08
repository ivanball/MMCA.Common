using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.MachineClients;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth.MachineClients;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Tests.Controllers.MachineClients;

/// <summary>
/// A real ASP.NET Core host over the machine-client controller bases (ADR-140): the framework's
/// <c>AddMachineClients</c> and <see cref="MachineClientService"/>, the real <see cref="TokenService"/>
/// (HS256) and JWT bearer validation, the framework's permission policies and
/// <see cref="SoftDeletedUserMiddleware"/> in the pipeline. Only the edges are doubles: the client rows
/// and the service accounts live in memory.
/// </summary>
internal sealed class MachineClientTestHost : IAsyncDisposable
{
    public const string Issuer = "machine-client-tests";
    public const string Audience = "machine-client-tests-api";

    /// <summary>A role a machine client may hold.</summary>
    public const string IntegrationRole = "Integrations";

    /// <summary>A role holding the impersonation permission, which no client may hold.</summary>
    public const string SupportRole = "Support";

    /// <summary>A role holding user management, which no client may hold.</summary>
    public const string UserAdminRole = "UserAdmin";

    /// <summary>The operator role that administers clients.</summary>
    public const string OperatorRole = "Operator";

    public const string OrdersRead = "orders:read";
    public const string OrdersWrite = "orders:write";

    /// <summary>The route the consumer's token controller is mounted at.</summary>
    public const string TokenPath = "auth/token";

    /// <summary>The route the consumer's administration controller is mounted at.</summary>
    public const string AdminPath = "admin/clients";

    /// <summary>An authenticated probe endpoint standing for any API a machine token calls.</summary>
    public const string ProbePath = "probe";

    private readonly WebApplication _app;

    private MachineClientTestHost(WebApplication app, InMemoryServiceAccounts accounts, InMemoryMachineClientStore store)
    {
        _app = app;
        Accounts = accounts;
        Store = store;
    }

    public InMemoryServiceAccounts Accounts { get; }

    public InMemoryMachineClientStore Store { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Starts a host. <paramref name="enabled"/> sets <c>MachineClients:Enabled</c>;
    /// <paramref name="addMachineClients"/> false skips <c>AddMachineClients</c> entirely.
    /// </summary>
    public static async Task<MachineClientTestHost> StartAsync(bool enabled = true, bool addMachineClients = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MachineClients:Enabled"] = enabled ? "true" : "false",
            ["MachineClients:AccessTokenMinutes"] = "10",
            ["Jwt:SigningAlgorithm"] = nameof(JwtSigningAlgorithm.HS256),
            ["Jwt:SecretForKey"] = signingKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });

        var accounts = new InMemoryServiceAccounts();
        var store = new InMemoryMachineClientStore();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCaching();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
        builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
        builder.Services.AddSingleton<ITokenService, TokenService>();
        builder.Services.AddScoped<IdempotencyFilter>();
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton<IServiceAccountProvisioner>(accounts);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IMachineClientStore>(store);
        if (addMachineClients)
        {
            builder.Services.AddMachineClients(builder.Configuration);
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            });
        builder.Services.AddAuthorizationPolicies();
        builder.Services.AddPermissions(grants => grants
            .Grant(IntegrationRole, OrdersRead, OrdersWrite)
            .Grant(SupportRole, OrdersRead, AdministrationPermissions.ViewAsUsers)
            .Grant(UserAdminRole, AdministrationPermissions.ManageUsers)
            .Grant(OperatorRole, AdministrationPermissions.ManageClients));
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new RoutedMachineClientControllersFeatureProvider());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<SoftDeletedUserMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet(ProbePath, (HttpContext context) => Results.Ok(context.User.Identity?.Name)).RequireAuthorization();
        await app.StartAsync(Xunit.TestContext.Current.CancellationToken);
        return new MachineClientTestHost(app, accounts, store);
    }

    /// <summary>An anonymous client.</summary>
    public HttpClient AnonymousClient() => _app.GetTestClient();

    /// <summary>A client presenting <paramref name="accessToken"/> as its bearer token.</summary>
    public HttpClient BearerClient(string accessToken)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>A client signed in as a person whose role administers machine clients.</summary>
    public HttpClient OperatorClient()
    {
        var tokens = _app.Services.GetRequiredService<ITokenService>();
        return BearerClient(tokens.GenerateAccessToken(900, "operator@example.com", OperatorRole, "Operator"));
    }

    /// <summary>Posts a form-encoded token request, the way an OAuth client library does.</summary>
    public async Task<HttpResponseMessage> RequestTokenAsync(
        string? clientId,
        string? clientSecret,
        string? grantType = "client_credentials",
        bool useBasic = false)
    {
        var client = AnonymousClient();
        var form = new List<KeyValuePair<string, string>>();
        if (grantType is not null)
        {
            form.Add(new("grant_type", grantType));
        }

        if (useBasic)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(clientId ?? string.Empty)}:{Uri.EscapeDataString(clientSecret ?? string.Empty)}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        else
        {
            if (clientId is not null)
            {
                form.Add(new("client_id", clientId));
            }

            if (clientSecret is not null)
            {
                form.Add(new("client_secret", clientSecret));
            }
        }

        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(new Uri(TokenPath, UriKind.Relative), content, Xunit.TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class RoutedMachineClientControllersFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Add(typeof(RoutedMachineTokenController).GetTypeInfo());
            feature.Controllers.Add(typeof(RoutedMachineClientsController).GetTypeInfo());
        }
    }
}

// Internal so the default controller discovery of other test hosts never picks them up. Each carries
// the class-level route a consumer's subclass declares.
[ApiController]
[Route(MachineClientTestHost.TokenPath)]
internal sealed class RoutedMachineTokenController : MachineTokenControllerBase;

[ApiController]
[Route(MachineClientTestHost.AdminPath)]
internal sealed class RoutedMachineClientsController : MachineClientsControllerBase;

/// <summary>The consumer's service accounts, in memory: create with a role and no password, read back by id.</summary>
internal sealed class InMemoryServiceAccounts : IServiceAccountProvisioner
{
    private readonly Dictionary<int, ServiceAccount> _accounts = [];
    private int _nextId = 5000;

    public Task<Result<UserIdentifierType>> CreateAsync(string name, string role, CancellationToken cancellationToken = default)
    {
        var id = ++_nextId;
        _accounts[id] = new ServiceAccount(id, name, string.Create(CultureInfo.InvariantCulture, $"svc-{id}@service.local"), role);
        return Task.FromResult(Result.Success<UserIdentifierType>(id));
    }

    public Task<ServiceAccount?> FindAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.GetValueOrDefault(userId));

    /// <summary>Changes an account's role behind the client's back (an operator re-roling the user).</summary>
    public void ChangeRole(UserIdentifierType userId, string role) =>
        _accounts[userId] = _accounts[userId] with { Role = role };
}

/// <summary>Client rows in memory; a row handed out is the stored instance, as an EF tracked entity would be.</summary>
internal sealed class InMemoryMachineClientStore : IMachineClientStore
{
    private readonly List<MachineClient> _rows = [];

    public IReadOnlyList<MachineClient> Rows => _rows;

    public Task<IReadOnlyList<MachineClient>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MachineClient>>([.. _rows]);

    public Task<MachineClient?> FindAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(r => r.Id == clientId));

    public Task AddAsync(MachineClient client, CancellationToken cancellationToken = default)
    {
        _rows.Add(client);
        return Task.CompletedTask;
    }

    public Task SaveAsync(MachineClient client, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordUseAsync(MachineClient client, DateTime usedOn, CancellationToken cancellationToken = default)
    {
        client.RecordUse(usedOn);
        return Task.CompletedTask;
    }
}
