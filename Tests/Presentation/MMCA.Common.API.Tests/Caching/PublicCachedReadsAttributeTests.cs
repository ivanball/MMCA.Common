using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Caching;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Tests.OpenApi;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.API.Tests.Caching;

/// <summary>
/// <c>[PublicCachedReads]</c> on an entity controller marks its inherited base reads (both
/// <c>GetAllAsync</c> overloads, <c>GetAllForLookupAsync</c>, <c>GetByIdAsync</c>) anonymous and
/// output-cached under one named policy, and names the by-id route, so a controller no longer
/// overrides a base read only to add attributes. Nothing else on the controller changes, and a
/// controller without the attribute is untouched.
/// </summary>
public sealed class PublicCachedReadsAttributeTests
{
    public const string ProbePolicy = "PublicReadsProbeCache";
    public const string OwnPolicy = "OwnProbeCache";
    public const string ExplicitRouteName = "ExplicitProbeById";
    public const string OverrideDeclaredRouteName = "OverrideDeclaredProbeById";

    private static readonly string[] ReadSuffixes = [string.Empty, "/paged", "/lookup", "/{id}"];

    // -- Rule 1 --
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_BlankPolicyName_Throws(string? policyName)
    {
        var act = () => new PublicCachedReadsAttribute(policyName!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_KeepsThePolicyName() =>
        new PublicCachedReadsAttribute(ProbePolicy).PolicyName.Should().Be(ProbePolicy);

    // -- Rule 2 --
    [Fact]
    public void Apply_ControllerNotDerivedFromEntityControllerBase_Throws()
    {
        var model = BuildControllerModel(typeof(NotAnEntityReadsProbeController));

        var act = () => new PublicCachedReadsAttribute(ProbePolicy).Apply(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(NotAnEntityReadsProbeController)).And.Contain("EntityControllerBase");
    }

    // -- Rule 7 --
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankByIdRouteName_Throws(string byIdRouteName)
    {
        var model = BuildControllerModel(typeof(PlainReadsProbeController));

        var act = () => new PublicCachedReadsAttribute(ProbePolicy) { ByIdRouteName = byIdRouteName }.Apply(model);

        act.Should().Throw<InvalidOperationException>();
    }

    // -- Rules 3, 4, 5 --
    [Fact]
    public async Task InheritedReads_CarryAllowAnonymousAndNamedOutputCache()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        foreach (var suffix in ReadSuffixes)
        {
            var endpoint = FindEndpoint(app, typeof(PublicReadsProbeController), "GET", suffix);

            endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().ContainSingle(
                "GET {0} is an inherited base read on a [PublicCachedReads] controller",
                suffix);
            endpoint.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().ContainSingle(
                "GET {0} is output-cached exactly once",
                suffix)
                .Which.PolicyName.Should().Be(ProbePolicy);
        }
    }

    [Fact]
    public async Task Export_And_Writes_GetNeitherMarker()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        foreach (var (method, suffix) in new[] { ("GET", "/export"), ("POST", string.Empty), ("PUT", "/{id}"), ("DELETE", "/{id}") })
        {
            var endpoint = FindEndpoint(app, typeof(PublicReadsProbeController), method, suffix);

            endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().BeEmpty(
                "{0} {1} is not one of the four base reads",
                method,
                suffix);
            endpoint.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().BeEmpty(
                "{0} {1} is not one of the four base reads",
                method,
                suffix);
        }
    }

    [Fact]
    public async Task AppDeclaredGetAction_GetsNeitherMarker()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var endpoint = FindEndpoint(app, typeof(PublicReadsProbeController), "GET", "/search");

        endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().BeEmpty("an app-declared action decides for itself");
        endpoint.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().BeEmpty("an app-declared action decides for itself");
    }

    [Fact]
    public async Task OverriddenRead_IsCovered_AndKeepsItsOwnOutputCachePolicy()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var lookup = FindEndpoint(app, typeof(OverridingReadsProbeController), "GET", "/lookup");
        lookup.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().ContainSingle("an overridden base read still qualifies");
        lookup.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().ContainSingle("an existing [OutputCache] wins")
            .Which.PolicyName.Should().Be(OwnPolicy);

        var byId = FindEndpoint(app, typeof(OverridingReadsProbeController), "GET", "/{id}");
        byId.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().ContainSingle(
            "an override declaring no [OutputCache] gets the controller's policy")
            .Which.PolicyName.Should().Be(ProbePolicy);
    }

    [Fact]
    public async Task OverriddenRead_WithOwnAllowAnonymous_GetsNoSecondOne()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var byId = FindEndpoint(app, typeof(OverridingReadsProbeController), "GET", "/{id}");

        byId.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().ContainSingle(
            "an [AllowAnonymous] the override already declares is not appended again");
    }

    // -- Rule 6 --
    [Fact]
    public async Task GetById_GetsDerivedRouteName_GetEntityNameById()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var byId = FindEndpoint(app, typeof(PublicReadsProbeController), "GET", "/{id}");

        RouteNameOf(byId).Should().Be($"Get{nameof(PublicReadsEntity)}ById");
    }

    [Fact]
    public async Task GetById_ExplicitByIdRouteName_IsUsed()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var byId = FindEndpoint(app, typeof(ExplicitNameReadsProbeController), "GET", "/{id}");

        RouteNameOf(byId).Should().Be(ExplicitRouteName);
    }

    [Fact]
    public async Task GetById_OverrideDeclaredName_IsKept()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        var byId = FindEndpoint(app, typeof(OverridingReadsProbeController), "GET", "/{id}");

        RouteNameOf(byId).Should().Be(OverrideDeclaredRouteName, "a name the override declared is kept");
    }

    [Fact]
    public async Task OnlyGetById_IsNamed()
    {
        await using WebApplication app = await CreateProbeHostAsync();

        foreach (var suffix in new[] { string.Empty, "/paged", "/lookup", "/export" })
        {
            RouteNameOf(FindEndpoint(app, typeof(MirrorReadsProbeController), "GET", suffix)).Should().BeNull(
                "only the by-id read gets a route name (GET {0})",
                suffix);
        }
    }

    // -- Rule 8 --
    [Fact]
    public async Task AttributedController_DiffersFromPlainOnlyByOperationId_AndAddsNoFilters()
    {
        await using WebApplication app = await CreateProbeHostAsync();
        var document = await ReadDocumentAsync(app);

        foreach (var suffix in ReadSuffixes.Append("/export"))
        {
            var attributed = FindEndpoint(app, typeof(MirrorReadsProbeController), "GET", suffix);
            var plain = FindEndpoint(app, typeof(PlainReadsProbeController), "GET", suffix);

            FilterTypesOf(attributed).Should().Equal(
                FilterTypesOf(plain),
                "the attribute adds endpoint metadata only, never an action filter (GET {0})",
                suffix);

            ComparableOperation(document, $"/MirrorReadsProbe{suffix}").Should().Be(
                ComparableOperation(document, $"/PlainReadsProbe{suffix}"),
                "parameters and responses are unchanged; only the operationId a route name produces may differ (GET {0})",
                suffix);
        }

        (Operation(document, "/MirrorReadsProbe/{id}")["operationId"]?.GetValue<string>()).Should().Be(
            $"Get{nameof(MirrorReadsEntity)}ById",
            "the operationId comes from the new route name (F4)");
    }

    // -- Rule 9 --
    // DESIGN-QUESTION: "identical document" has no pre-change baseline in a test-first run; pinned as "the plain controller documents identically alone and beside attributed controllers".
    [Fact]
    public async Task ControllerWithoutAttribute_DocumentIsUnchanged()
    {
        await using WebApplication alone = await OpenApiProbeHost.CreateAsync(typeof(PlainReadsProbeController));
        await using WebApplication beside = await CreateProbeHostAsync();

        var aloneDocument = await ReadDocumentAsync(alone);
        var besideDocument = await ReadDocumentAsync(beside);

        foreach (var suffix in ReadSuffixes.Append("/export"))
        {
            var path = $"/PlainReadsProbe{suffix}";
            Operation(besideDocument, path).ToJsonString().Should().Be(
                Operation(aloneDocument, path).ToJsonString(),
                "a controller without the attribute documents exactly as before ({0})",
                path);

            var endpoint = FindEndpoint(beside, typeof(PlainReadsProbeController), "GET", suffix);
            endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>().Should().BeEmpty();
            endpoint.Metadata.GetOrderedMetadata<OutputCacheAttribute>().Should().BeEmpty();
            RouteNameOf(endpoint).Should().BeNull();
        }
    }

    // -- Rule 4 end to end --
    [Fact]
    public async Task AnonymousGet_Succeeds_AnonymousPost_Is401()
    {
        var queryService = new Mock<IEntityQueryService<AuthReadsEntity, PublicReadsDto, int>>();
        queryService
            .Setup(q => q.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<AuthReadsEntity, int>?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<object>(new PublicReadsDto { Id = 1 }));

        await using WebApplication app = await CreateAuthenticatedHostAsync(queryService.Object);
        using HttpClient client = app.GetTestClient();

        using var get = await client.GetAsync(new Uri("/AuthReadsProbe/1", UriKind.Relative), TestContext.Current.CancellationToken);
        using var post = await client.PostAsync(new Uri("/AuthReadsProbe", UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        get.StatusCode.Should().Be(HttpStatusCode.OK, "the inherited by-id read is anonymous under [PublicCachedReads]");
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the class-level [Authorize] still gates every other action");
    }

    // -- Helpers --
    private static Task<WebApplication> CreateProbeHostAsync() =>
        OpenApiProbeHost.CreateAsync(
            typeof(PublicReadsProbeController),
            typeof(OverridingReadsProbeController),
            typeof(ExplicitNameReadsProbeController),
            typeof(MirrorReadsProbeController),
            typeof(PlainReadsProbeController));

    /// <summary>A minimal application model: the controller plus one attribute-routed selector per public action.</summary>
    private static ControllerModel BuildControllerModel(Type controllerType)
    {
        var controller = new ControllerModel(controllerType.GetTypeInfo(), controllerType.GetCustomAttributes(inherit: true));

        var actions = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ControllerBase) && !m.IsSpecialName);

        foreach (var method in actions)
        {
            var action = new ActionModel(method, method.GetCustomAttributes(inherit: true)) { Controller = controller };
            action.Selectors.Add(new SelectorModel { AttributeRouteModel = new AttributeRouteModel { Template = method.Name } });
            controller.Actions.Add(action);
        }

        return controller;
    }

    private static Endpoint FindEndpoint(WebApplication app, Type controllerType, string httpMethod, string suffix)
    {
        var controllerName = controllerType.Name[..^"Controller".Length];
        var template = controllerName + suffix;

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == controllerType)
            .Single(e =>
                e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(httpMethod, StringComparer.OrdinalIgnoreCase) == true
                && string.Equals(
                    e.Metadata.GetMetadata<ControllerActionDescriptor>()!.AttributeRouteInfo?.Template,
                    template,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string? RouteNameOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName;

    private static Type[] FilterTypesOf(Endpoint endpoint) =>
        [.. endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()!.FilterDescriptors.Select(f => f.Filter.GetType())];

    private static async Task<JsonNode> ReadDocumentAsync(WebApplication app)
    {
        using HttpClient client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The GET operation at <paramref name="path"/>, matching the path key case-insensitively.</summary>
    private static JsonNode Operation(JsonNode document, string path)
    {
        var paths = document["paths"]!.AsObject();
        var key = paths.Select(p => p.Key).Single(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

        return paths[key]!["get"] ?? throw new InvalidOperationException($"The document has no GET operation at {path}.");
    }

    /// <summary>The operation without the two members that legitimately differ between two controllers.</summary>
    private static string ComparableOperation(JsonNode document, string path)
    {
        var operation = Operation(document, path).DeepClone().AsObject();
        operation.Remove("operationId");
        operation.Remove("tags");
        return operation.ToJsonString();
    }

    private static async Task<WebApplication> CreateAuthenticatedHostAsync(
        IEntityQueryService<AuthReadsEntity, PublicReadsDto, int> queryService)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(queryService);
        builder.Services
            .AddAuthentication(NoUserAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, NoUserAuthenticationHandler>(NoUserAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();
        builder.Services.AddOutputCache(options => options.AddPublicEndpointPolicy(ProbePolicy, TimeSpan.FromMinutes(5), "probe"));
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new AuthProbeControllerFeatureProvider());
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        app.MapControllers();
        await app.StartAsync();

        return app;
    }

    private sealed class AuthProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(AuthReadsProbeController).GetTypeInfo());
    }

    /// <summary>Never authenticates anyone, so every request is anonymous and a challenge answers 401.</summary>
    private sealed class NoUserAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "NoUser";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

/// <summary>The DTO every probe serves.</summary>
public sealed record PublicReadsDto : IBaseDTO<int>
{
    public required int Id { get; init; }
}

/// <summary>Aggregate behind the CRUD probe.</summary>
public sealed class PublicReadsEntity : AuditableAggregateRootEntity<int>;

/// <summary>Entity behind the overriding probe.</summary>
public sealed class OverrideReadsEntity : AuditableBaseEntity<int>;

/// <summary>Entity behind the explicit-route-name probe.</summary>
public sealed class ExplicitReadsEntity : AuditableBaseEntity<int>;

/// <summary>Entity behind the attributed half of the mirror pair.</summary>
public sealed class MirrorReadsEntity : AuditableBaseEntity<int>;

/// <summary>Entity behind the unattributed half of the mirror pair.</summary>
public sealed class PlainReadsEntity : AuditableBaseEntity<int>;

/// <summary>Entity behind the authenticated-host probe.</summary>
public sealed class AuthReadsEntity : AuditableBaseEntity<int>;

public sealed record PublicReadsCreateRequest : ICreateRequest;

public sealed record PublicReadsUpdateRequest;

/// <summary>
/// A CRUD controller with the attribute: four inherited base reads, the inherited export and writes,
/// and one app-declared GET.
/// </summary>
[PublicCachedReads(PublicCachedReadsAttributeTests.ProbePolicy)]
public sealed class PublicReadsProbeController(
    IEntityQueryService<PublicReadsEntity, PublicReadsDto, int> queryService,
    ICommandHandler<PublicReadsCreateRequest, Result<PublicReadsDto>> createHandler,
    ICommandHandler<UpdateEntityCommand<PublicReadsEntity, PublicReadsUpdateRequest, int>, Result<PublicReadsDto>> updateHandler,
    ICommandHandler<DeleteEntityCommand<PublicReadsEntity, int>, Result> deleteHandler,
    ILogger<EntityControllerBase<PublicReadsEntity, PublicReadsDto, int>> logger)
    : CrudEntityControllerBase<PublicReadsEntity, PublicReadsDto, int, PublicReadsCreateRequest, PublicReadsUpdateRequest>(
        queryService, createHandler, updateHandler, deleteHandler, logger)
{
    [HttpGet("search")]
    public IActionResult Search() => Ok();
}

/// <summary>Overrides two base reads: one with its own output-cache policy, one with its own route name and [AllowAnonymous].</summary>
[PublicCachedReads(PublicCachedReadsAttributeTests.ProbePolicy)]
public sealed class OverridingReadsProbeController(
    IEntityQueryService<OverrideReadsEntity, PublicReadsDto, int> queryService,
    ILogger<EntityControllerBase<OverrideReadsEntity, PublicReadsDto, int>> logger)
    : EntityControllerBase<OverrideReadsEntity, PublicReadsDto, int>(queryService, logger)
{
    [OutputCache(PolicyName = PublicCachedReadsAttributeTests.OwnPolicy)]
    public override Task<ActionResult<CollectionResult<BaseLookup<int>>>> GetAllForLookupAsync(
        [Required] string nameProperty,
        CancellationToken cancellationToken = default) =>
        base.GetAllForLookupAsync(nameProperty, cancellationToken);

    [HttpGet("{id}", Name = PublicCachedReadsAttributeTests.OverrideDeclaredRouteName)]
    [AllowAnonymous]
    public override Task<ActionResult<PublicReadsDto>> GetByIdAsync(
        int id,
        bool includeFKs = true,
        bool includeChildren = false,
        [FromQuery] string? fields = null,
        CancellationToken cancellationToken = default) =>
        base.GetByIdAsync(id, includeFKs, includeChildren, fields, cancellationToken);
}

/// <summary>Names its by-id route explicitly.</summary>
[PublicCachedReads(PublicCachedReadsAttributeTests.ProbePolicy, ByIdRouteName = PublicCachedReadsAttributeTests.ExplicitRouteName)]
public sealed class ExplicitNameReadsProbeController(
    IEntityQueryService<ExplicitReadsEntity, PublicReadsDto, int> queryService,
    ILogger<EntityControllerBase<ExplicitReadsEntity, PublicReadsDto, int>> logger)
    : EntityControllerBase<ExplicitReadsEntity, PublicReadsDto, int>(queryService, logger);

/// <summary>The attributed half of a structurally identical pair.</summary>
[PublicCachedReads(PublicCachedReadsAttributeTests.ProbePolicy)]
public sealed class MirrorReadsProbeController(
    IEntityQueryService<MirrorReadsEntity, PublicReadsDto, int> queryService,
    ILogger<EntityControllerBase<MirrorReadsEntity, PublicReadsDto, int>> logger)
    : EntityControllerBase<MirrorReadsEntity, PublicReadsDto, int>(queryService, logger);

/// <summary>The unattributed half of the pair: must come out exactly as it would with no attribute anywhere.</summary>
public sealed class PlainReadsProbeController(
    IEntityQueryService<PlainReadsEntity, PublicReadsDto, int> queryService,
    ILogger<EntityControllerBase<PlainReadsEntity, PublicReadsDto, int>> logger)
    : EntityControllerBase<PlainReadsEntity, PublicReadsDto, int>(queryService, logger);

/// <summary>Not an entity controller: the attribute has no base reads to mark.</summary>
public sealed class NotAnEntityReadsProbeController : ApiControllerBase
{
    [HttpGet("probe")]
    public IActionResult Get() => Ok();
}

/// <summary>Authenticated by default; the attribute opens the inherited reads only.</summary>
[Authorize]
[PublicCachedReads(PublicCachedReadsAttributeTests.ProbePolicy)]
public sealed class AuthReadsProbeController(
    IEntityQueryService<AuthReadsEntity, PublicReadsDto, int> queryService,
    ILogger<EntityControllerBase<AuthReadsEntity, PublicReadsDto, int>> logger)
    : EntityControllerBase<AuthReadsEntity, PublicReadsDto, int>(queryService, logger)
{
    [HttpPost]
    public IActionResult Touch() => NoContent();
}
