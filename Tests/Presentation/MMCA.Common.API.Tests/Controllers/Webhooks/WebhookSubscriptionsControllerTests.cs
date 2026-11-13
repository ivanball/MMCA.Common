using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
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
using MMCA.Common.API.Controllers.Webhooks;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Webhooks;

namespace MMCA.Common.API.Tests.Controllers.Webhooks;

/// <summary>
/// <see cref="WebhookSubscriptionsControllerBase"/> through real routing, JWT bearer validation and the
/// framework's permission policies (design 35(a) API, rules 35.14, 35.17 and 35.18), the shape of the
/// machine-client controller host. Only the <see cref="IWebhookSubscriptionService"/> is a double:
/// the controller's job is the 404 gate, the <c>webhooks:manage</c> permission and the mapping of each
/// result to a status code, and the secret must appear only in the create and rotate responses.
/// </summary>
public sealed class WebhookSubscriptionsControllerTests
{
    // Rule 35.18: off (or never registered) means every action answers 404.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotEnabled_EveryAction404(bool registerService)
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync(registerService ? false : null);
        var client = host.OperatorClient();
        var id = Guid.NewGuid();
        var path = WebhookSubscriptionsTestHost.AdminPath;

        using var list = await client.GetAsync(Relative(path), Ct);
        using var eventTypes = await client.GetAsync(Relative($"{path}/event-types"), Ct);
        using var create = await client.PostAsJsonAsync(Relative(path), NewCreateRequest(), Ct);
        using var update = await client.PutAsJsonAsync(Relative($"{path}/{id}"), NewUpdateRequest(), Ct);
        using var rotate = await client.PostAsync(Relative($"{path}/{id}/rotate-secret"), null, Ct);
        using var disable = await client.PostAsync(Relative($"{path}/{id}/disable"), null, Ct);
        using var enable = await client.PostAsync(Relative($"{path}/{id}/enable"), null, Ct);
        using var delete = await client.DeleteAsync(Relative($"{path}/{id}"), Ct);

        new[] { list, eventTypes, create, update, rotate, disable, enable, delete }.Select(r => r.StatusCode)
            .Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        host.Service?.Calls.Should().Be(0, "a disabled service is never asked to do anything");
    }

    [Fact]
    public async Task WithoutPermission_403()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.ClientFor(WebhookSubscriptionsTestHost.TicketsRole);
        var path = WebhookSubscriptionsTestHost.AdminPath;

        using var list = await client.GetAsync(Relative(path), Ct);
        using var create = await client.PostAsJsonAsync(Relative(path), NewCreateRequest(), Ct);
        using var delete = await client.DeleteAsync(Relative($"{path}/{Guid.NewGuid()}"), Ct);

        new[] { list, create, delete }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(
            HttpStatusCode.Forbidden, "every action requires webhooks:manage");
        host.Service!.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Anonymous_401()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();

        using var response = await host.AnonymousClient().GetAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_200WithSecret()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();

        using var response = await host.OperatorClient().PostAsJsonAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), NewCreateRequest(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionSecretResponse>(Ct))!;
        created.Secret.Should().Be(FakeWebhookSubscriptionService.IssuedSecret, "the create response carries the plaintext secret, once");
        created.Subscription.Url.Should().Be("https://hooks.example.com/in");
        created.Subscription.EventTypes.Should().Equal("ticket.opened");
        created.Subscription.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_HasNoSecretField()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.OperatorClient();
        using (var create = await client.PostAsJsonAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), NewCreateRequest(), Ct))
        {
            create.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var response = await client.GetAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain(FakeWebhookSubscriptionService.IssuedSecret);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetArrayLength().Should().Be(1);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            row.EnumerateObject().Select(p => p.Name).Should().NotContain(
                name => name.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "a listed subscription carries neither the secret nor its protected form (rule 35.14)");
        }
    }

    [Fact]
    public async Task EventTypes_ListsCatalog()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();

        using var response = await host.OperatorClient().GetAsync(Relative($"{WebhookSubscriptionsTestHost.AdminPath}/event-types"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<string[]>(Ct)).Should().BeEquivalentTo(FakeWebhookSubscriptionService.CatalogEventTypes);
    }

    [Fact]
    public async Task Delete_204()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.OperatorClient();
        var id = await CreateAsync(client);

        using var first = await client.DeleteAsync(Relative($"{WebhookSubscriptionsTestHost.AdminPath}/{id}"), Ct);
        using var second = await client.DeleteAsync(Relative($"{WebhookSubscriptionsTestHost.AdminPath}/{id}"), Ct);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound, "a deleted id answers NotFound (rule 35.17)");
    }

    [Fact]
    public async Task RotateSecret_200WithNewSecret()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.OperatorClient();
        var id = await CreateAsync(client);

        using var response = await client.PostAsync(Relative($"{WebhookSubscriptionsTestHost.AdminPath}/{id}/rotate-secret"), null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionSecretResponse>(Ct))!;
        rotated.Secret.Should().Be(FakeWebhookSubscriptionService.RotatedSecret);
        rotated.Subscription.Id.Should().Be(id);
    }

    [Fact]
    public async Task UpdateDisableEnable_200WithoutSecret()
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.OperatorClient();
        var id = await CreateAsync(client);
        var path = $"{WebhookSubscriptionsTestHost.AdminPath}/{id}";

        using var update = await client.PutAsJsonAsync(Relative(path), NewUpdateRequest(), Ct);
        using var disable = await client.PostAsync(Relative($"{path}/disable"), null, Ct);
        var disabledText = await disable.Content.ReadAsStringAsync(Ct);
        using var enable = await client.PostAsync(Relative($"{path}/enable"), null, Ct);

        new[] { update, disable, enable }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<WebhookSubscriptionResponse>(Ct))!.Url.Should().Be("https://hooks.example.com/v2");
        using var disabledJson = JsonDocument.Parse(disabledText);
        disabledJson.RootElement.GetProperty("isEnabled").GetBoolean().Should().BeFalse();
        (await enable.Content.ReadFromJsonAsync<WebhookSubscriptionResponse>(Ct))!.IsEnabled.Should().BeTrue();
        disabledText.Should().NotContain(FakeWebhookSubscriptionService.IssuedSecret, "only create and rotate return a secret");
    }

    [Theory]
    [InlineData("update")]
    [InlineData("rotate-secret")]
    [InlineData("disable")]
    [InlineData("enable")]
    public async Task UnknownId_404(string action)
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        var client = host.OperatorClient();
        var path = $"{WebhookSubscriptionsTestHost.AdminPath}/{Guid.NewGuid()}";

        using var response = action == "update"
            ? await client.PutAsJsonAsync(Relative(path), NewUpdateRequest(), Ct)
            : await client.PostAsync(Relative($"{path}/{action}"), null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's id and an unknown id look the same (rule 35.17)");
    }

    [Theory]
    [InlineData(WebhookErrorCodes.TargetNotAllowed, HttpStatusCode.BadRequest)]
    [InlineData(WebhookErrorCodes.EventTypeUnknown, HttpStatusCode.BadRequest)]
    [InlineData(WebhookErrorCodes.LimitReached, HttpStatusCode.Conflict)]
    public async Task Create_Refusal_MapsToItsStatus(string code, HttpStatusCode expected)
    {
        await using var host = await WebhookSubscriptionsTestHost.StartAsync();
        host.Service!.CreateFailure = expected == HttpStatusCode.Conflict
            ? Error.Conflict(code, "Refused.")
            : Error.Validation(code, "Refused.");

        using var response = await host.OperatorClient().PostAsJsonAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), NewCreateRequest(), Ct);

        response.StatusCode.Should().Be(expected);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(code);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static CreateWebhookSubscriptionRequest NewCreateRequest() =>
        new("https://hooks.example.com/in", "Ticket feed", ["ticket.opened"]);

    private static UpdateWebhookSubscriptionRequest NewUpdateRequest() =>
        new("https://hooks.example.com/v2", "Ticket feed v2", ["ticket.opened", "ticket.closed"]);

    private static async Task<Guid> CreateAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(Relative(WebhookSubscriptionsTestHost.AdminPath), NewCreateRequest(), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<WebhookSubscriptionSecretResponse>(Ct))!.Subscription.Id;
    }
}

/// <summary>
/// A real ASP.NET Core host over <see cref="WebhookSubscriptionsControllerBase"/>: the real
/// <see cref="TokenService"/> (HS256), JWT bearer validation and the framework's permission policies.
/// <c>serviceEnabled</c> null registers no <see cref="IWebhookSubscriptionService"/> at all.
/// </summary>
internal sealed class WebhookSubscriptionsTestHost : IAsyncDisposable
{
    public const string Issuer = "webhook-tests";
    public const string Audience = "webhook-tests-api";

    /// <summary>The operator role that administers webhooks.</summary>
    public const string OperatorRole = "Operator";

    /// <summary>A role with an unrelated permission only.</summary>
    public const string TicketsRole = "Tickets";

    /// <summary>The route the consumer's derived controller is mounted at.</summary>
    public const string AdminPath = "admin/webhooks";

    private readonly WebApplication _app;

    private WebhookSubscriptionsTestHost(WebApplication app, FakeWebhookSubscriptionService? service)
    {
        _app = app;
        Service = service;
    }

    public FakeWebhookSubscriptionService? Service { get; }

    public static async Task<WebhookSubscriptionsTestHost> StartAsync(bool? serviceEnabled = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Jwt:SigningAlgorithm"] = nameof(JwtSigningAlgorithm.HS256),
            ["Jwt:SecretForKey"] = signingKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });

        FakeWebhookSubscriptionService? service = null;
        if (serviceEnabled is { } enabled)
        {
            service = new FakeWebhookSubscriptionService { IsEnabled = enabled };
            builder.Services.AddSingleton<IWebhookSubscriptionService>(service);
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCaching();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
        builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
        builder.Services.AddSingleton<ITokenService, TokenService>();
        builder.Services.AddScoped<IdempotencyFilter>();
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
            .Grant(OperatorRole, AdministrationPermissions.ManageWebhooks)
            .Grant(TicketsRole, "tickets:read"));
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new RoutedWebhookControllersFeatureProvider());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new WebhookSubscriptionsTestHost(app, service);
    }

    public HttpClient AnonymousClient() => _app.GetTestClient();

    /// <summary>A client signed in as a person holding <paramref name="role"/>.</summary>
    public HttpClient ClientFor(string role)
    {
        var tokens = _app.Services.GetRequiredService<ITokenService>();
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.GenerateAccessToken(901, "person@example.com", role, "Person"));
        return client;
    }

    /// <summary>A client whose role holds <c>webhooks:manage</c>.</summary>
    public HttpClient OperatorClient() => ClientFor(OperatorRole);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class RoutedWebhookControllersFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(RoutedWebhookSubscriptionsController).GetTypeInfo());
    }
}

// Internal so the default controller discovery of other test hosts never picks it up. It carries the
// class-level route a consumer's subclass declares, and nothing else (design 35(d)).
[ApiController]
[Route(WebhookSubscriptionsTestHost.AdminPath)]
internal sealed class RoutedWebhookSubscriptionsController : WebhookSubscriptionsControllerBase;

/// <summary>Subscriptions in memory, answering the way the real service's contract says.</summary>
internal sealed class FakeWebhookSubscriptionService : IWebhookSubscriptionService
{
    public const string IssuedSecret = "whsec_SXNzdWVkT25jZUF0Q3JlYXRlVGhpc0lzVGhlT25seUNvcHk=";
    public const string RotatedSecret = "whsec_Um90YXRlZEFuZFJldHVybmVkT25jZVRoaXNJc05ldw==";

    private readonly Dictionary<Guid, WebhookSubscriptionResponse> _rows = [];

    public static IReadOnlyCollection<string> CatalogEventTypes { get; } = ["ticket.closed", "ticket.opened"];

    public bool IsEnabled { get; init; } = true;

    public IReadOnlyCollection<string> EventTypes => Count(CatalogEventTypes);

    /// <summary>Gets the number of calls to the methods and to <see cref="EventTypes"/> (reading <see cref="IsEnabled"/> is not counted).</summary>
    public int Calls { get; private set; }

    /// <summary>Gets or sets an error the next create answers with.</summary>
    public Error? CreateFailure { get; set; }

    public Task<Result<IReadOnlyList<WebhookSubscriptionResponse>>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<WebhookSubscriptionResponse>>([.. Count(_rows.Values)]));

    public Task<Result<WebhookSubscriptionSecretResponse>> CreateAsync(CreateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (CreateFailure is { } failure)
        {
            return Task.FromResult(Result.Failure<WebhookSubscriptionSecretResponse>(failure));
        }

        var row = new WebhookSubscriptionResponse(
            Guid.NewGuid(), request.Url, request.Description, [.. request.EventTypes], IsEnabled: true, DateTime.UtcNow, null, null, null);
        _rows[row.Id] = row;
        return Task.FromResult(Result.Success(new WebhookSubscriptionSecretResponse(row, IssuedSecret)));
    }

    public Task<Result<WebhookSubscriptionResponse>> UpdateAsync(Guid id, UpdateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default) =>
        Change(id, row => row with { Url = request.Url, Description = request.Description, EventTypes = [.. request.EventTypes] });

    public Task<Result<WebhookSubscriptionSecretResponse>> RotateSecretAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_rows.TryGetValue(id, out var row)
            ? Result.Success(new WebhookSubscriptionSecretResponse(row, RotatedSecret))
            : Result.Failure<WebhookSubscriptionSecretResponse>(NotFound()));
    }

    public Task<Result<WebhookSubscriptionResponse>> DisableAsync(Guid id, CancellationToken cancellationToken = default) =>
        Change(id, row => row with { IsEnabled = false });

    public Task<Result<WebhookSubscriptionResponse>> EnableAsync(Guid id, CancellationToken cancellationToken = default) =>
        Change(id, row => row with { IsEnabled = true });

    public Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_rows.Remove(id) ? Result.Success() : Result.Failure(NotFound()));
    }

    private static Error NotFound() => Error.NotFoundError(WebhookErrorCodes.NotFound, "No such webhook subscription.");

    private T Count<T>(T value)
    {
        Calls++;
        return value;
    }

    private Task<Result<WebhookSubscriptionResponse>> Change(Guid id, Func<WebhookSubscriptionResponse, WebhookSubscriptionResponse> change)
    {
        Calls++;
        if (!_rows.TryGetValue(id, out var row))
        {
            return Task.FromResult(Result.Failure<WebhookSubscriptionResponse>(NotFound()));
        }

        var changed = change(row);
        _rows[id] = changed;
        return Task.FromResult(Result.Success(changed));
    }
}
