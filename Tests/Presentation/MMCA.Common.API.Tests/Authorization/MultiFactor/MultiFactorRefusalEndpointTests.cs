using System.Globalization;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.API.Localization;
using MMCA.Common.Shared.Auth;
using static MMCA.Common.API.Tests.Authorization.MultiFactor.MultiFactorTestPrincipals;

namespace MMCA.Common.API.Tests.Authorization.MultiFactor;

/// <summary>
/// The role-level second factor at the HTTP boundary, over a real host: the refusal is a coded,
/// localized, uncacheable <c>403</c> Problem Details document on a permission endpoint; a plain
/// <c>[Authorize]</c> endpoint, a caller with the factor, a host with no list and a gRPC request all
/// keep the answer they had; and a result handler the host registered itself still handles every
/// other outcome.
/// </summary>
public sealed class MultiFactorRefusalEndpointTests
{
    private const string PermissionPath = "/orders";
    private const string AuthenticatedPath = "/profile";
    private const string LocalizedMessage = "Esta operacion requiere un segundo factor.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PermissionEndpoint_ListedRoleWithoutASecondFactor_Answers403WithTheCodedProblemDocument(bool problemDetailsService)
    {
        await using var app = await StartAsync(requiredForRoles: [Admin], problemDetailsService: problemDetailsService);
        using var client = ClientFor(app, Role(Admin));

        using var response = await client.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status403Forbidden);
        root.GetProperty("type").GetString().Should().Be("https://tools.ietf.org/html/rfc9110#section-15.5.4");
        root.GetProperty("title").GetString().Should().Be("Second factor required");
        root.GetProperty("detail").GetString().Should().Be(LocalizedMessage);
        var error = root.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("code").GetString().Should().Be(AuthErrorCodes.MultiFactorRequired);
        error.GetProperty("message").GetString().Should().Be(LocalizedMessage, "the message goes through the host's error localizer");
        error.GetProperty("type").GetString().Should().Be("Forbidden");
        error.GetProperty("source").GetString().Should().Be("HasPermission");
    }

    [Fact]
    public async Task PermissionEndpoint_ListedRoleWithASecondFactor_Answers200()
    {
        await using var app = await StartAsync(requiredForRoles: [Admin]);
        using var client = ClientFor(app, Role(Admin), SecondFactor());

        using var response = await client.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_ListedRoleWithoutASecondFactor_Answers200()
    {
        await using var app = await StartAsync(requiredForRoles: [Admin]);
        using var client = ClientFor(app, Role(Admin));

        using var response = await client.GetAsync(new Uri(AuthenticatedPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a plain [Authorize] endpoint (the enrollment endpoints) stays reachable");
    }

    [Theory]
    [InlineData(PermissionPath)]
    [InlineData(AuthenticatedPath)]
    public async Task EmptyList_ListedRoleWithoutASecondFactor_Answers200(string path)
    {
        await using var app = await StartAsync(requiredForRoles: []);
        using var client = ClientFor(app, Role(Admin));

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PermissionEndpoint_CallerLackingThePermission_KeepsTheBodylessForbid()
    {
        await using var app = await StartAsync(requiredForRoles: [Admin, Auditor]);
        using var client = ClientFor(app, Role(Auditor));

        using var response = await client.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty("the plain refusal says nothing about a step-up");
        response.Headers.CacheControl.Should().BeNull();
    }

    [Fact]
    public async Task PermissionEndpoint_GrpcRequestWithoutASecondFactor_KeepsTheDefaultForbid()
    {
        await using var app = await StartAsync(requiredForRoles: [Admin]);
        using var client = ClientFor(app, Role(Admin));
        using var content = new ByteArrayContent([0, 0, 0, 0, 0]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

        using var response = await client.PostAsync(new Uri(PermissionPath, UriKind.Relative), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty("a JSON body would break the gRPC framing");
    }

    [Fact]
    public async Task HostResultHandlerRegisteredFirst_StillHandlesEveryOtherOutcome_ButNotTheMultiFactorRefusal()
    {
        var recorder = new RecordingResultHandler();
        await using var app = await StartAsync(requiredForRoles: [Admin, Auditor], hostResultHandler: recorder);

        using (var lacking = ClientFor(app, Role(Auditor)))
        using (var forbidden = await lacking.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            forbidden.Headers.Should().ContainKey(RecordingResultHandler.Header, "the host's handler answered the plain refusal");
        }

        using (var anonymous = app.GetTestClient())
        using (var challenged = await anonymous.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct))
        {
            challenged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var stepped = ClientFor(app, Role(Admin), SecondFactor()))
        using (var granted = await stepped.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct))
        {
            granted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        recorder.Outcomes.Should().Equal("forbidden", "challenged", "succeeded");

        using var admin = ClientFor(app, Role(Admin));
        using var refused = await admin.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        recorder.Outcomes.Should().HaveCount(3, "the coded refusal is written without reaching the host's handler");
    }

    [Fact]
    public async Task AddAuthorizationPoliciesCalledTwice_StillAnswersTheCodedRefusal_AndGrantsTheOthers()
    {
        await using var app = await StartAsync(requiredForRoles: [Admin], registerPoliciesTwice: true);

        using var admin = ClientFor(app, Role(Admin));
        using var refused = await admin.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);
        using var member = ClientFor(app, Role(Member));
        using var granted = await member.GetAsync(new Uri(PermissionPath, UriKind.Relative), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void AddAuthorizationPoliciesCalledTwice_WrapsTheResultHandlerOnce()
    {
        // Each wrapper layer asks the container for its logger once when it is built, so the number of
        // asks per resolution is the number of layers.
        var loggerRequests = 0;
        var services = new ServiceCollection();
        services.AddTransient<ILogger<MultiFactorRefusalResultHandler>>(_ =>
        {
            loggerRequests++;
            return NullLogger<MultiFactorRefusalResultHandler>.Instance;
        });
        services.AddAuthorizationPolicies();
        services.AddAuthorizationPolicies();
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IAuthorizationMiddlewareResultHandler>();

        handler.Should().BeOfType<MultiFactorRefusalResultHandler>();
        loggerRequests.Should().Be(1, "a second AddAuthorizationPolicies call must not wrap the wrapper");
        services.Count(d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler)).Should().Be(1);
    }

    private static HttpClient ClientFor(WebApplication app, params System.Security.Claims.Claim[] claims)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ClaimsHeader, Header([new System.Security.Claims.Claim("sub", "7"), .. claims]));
        return client;
    }

    private static async Task<WebApplication> StartAsync(
        string[] requiredForRoles,
        bool problemDetailsService = true,
        IAuthorizationMiddlewareResultHandler? hostResultHandler = null,
        bool registerPoliciesTwice = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(requiredForRoles
            .Select((role, index) => new KeyValuePair<string, string?>(
                string.Create(CultureInfo.InvariantCulture, $"{MultiFactorAuthorizationSettings.SectionName}:RequiredForRoles:{index}"),
                role)));

        if (hostResultHandler is not null)
        {
            builder.Services.AddSingleton(hostResultHandler);
        }

        if (problemDetailsService)
        {
            builder.Services.AddProblemDetails();
        }

        builder.Services.AddSingleton<IErrorLocalizer>(new SpanishErrorLocalizer());
        builder.Services.AddAuthentication(SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HeaderClaimsAuthenticationHandler>(SchemeName, configureOptions: null);
        builder.Services.AddAuthorizationPolicies();
        if (registerPoliciesTwice)
        {
            builder.Services.AddAuthorizationPolicies();
        }

        builder.Services.AddPermissions(grants => grants
            .Grant(Admin, Permission)
            .Grant(Member, Permission));

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMethods(PermissionPath, [HttpMethods.Get, HttpMethods.Post], () => Results.Text("orders"))
            .RequireAuthorization(new HasPermissionAttribute(Permission));
        app.MapGet(AuthenticatedPath, () => Results.Text("profile")).RequireAuthorization();
        await app.StartAsync(Ct);

        return app;
    }

    /// <summary>Answers the multi-factor code with a fixed Spanish text and every other code with its fallback.</summary>
    private sealed class SpanishErrorLocalizer : IErrorLocalizer
    {
        public string Localize(string code, string fallbackMessage) =>
            string.Equals(code, AuthErrorCodes.MultiFactorRequired, StringComparison.Ordinal) ? LocalizedMessage : fallbackMessage;
    }

    /// <summary>A result handler a host registers before the framework: records each outcome, then answers as the default does.</summary>
    private sealed class RecordingResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public const string Header = "X-Host-Result-Handler";

        private readonly AuthorizationMiddlewareResultHandler _default = new();
        private readonly List<string> _outcomes = [];

        public IReadOnlyList<string> Outcomes
        {
            get
            {
                lock (_outcomes)
                {
                    return [.. _outcomes];
                }
            }
        }

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            var outcome = "succeeded";
            if (authorizeResult.Forbidden)
            {
                outcome = "forbidden";
            }
            else if (authorizeResult.Challenged)
            {
                outcome = "challenged";
            }

            lock (_outcomes)
            {
                _outcomes.Add(outcome);
            }

            context.Response.Headers[Header] = outcome;
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
