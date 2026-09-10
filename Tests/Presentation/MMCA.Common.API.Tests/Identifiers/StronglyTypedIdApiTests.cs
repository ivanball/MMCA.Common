using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Endpoints;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Identifiers;
using Moq;

namespace MMCA.Common.API.Tests.Identifiers;

/// <summary>
/// The HTTP edge of ADR-115, over a started in-memory host running the real
/// <c>AddAPI</c> + <c>AddCommonOpenApi</c> + <c>MapCommonOpenApi</c> pipeline: a wrapper binds from
/// a route segment and a query string, serializes as the bare primitive, and is documented as the
/// primitive rather than as an object.
/// </summary>
public sealed class StronglyTypedIdApiTests
{
    [Fact]
    public async Task RouteSegment_BindsAWrappedIdentifier()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            new Uri("wrapped/42", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("\"id\":42").And.NotContain("value", "the wrapper is not an object on the wire");
    }

    [Fact]
    public async Task RouteSegment_RejectsAValueThatIsNotTheWrappedPrimitive()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(
            new Uri("wrapped/abc", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryString_BindsAnOptionalWrappedIdentifier()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        var withParent = await client.GetFromJsonAsync<JsonElement>(
            new Uri("wrapped?parentId=7", UriKind.Relative),
            TestContext.Current.CancellationToken);
        withParent.GetProperty("parentId").GetInt32().Should().Be(7);

        var withoutParent = await client.GetFromJsonAsync<JsonElement>(
            new Uri("wrapped", UriKind.Relative),
            TestContext.Current.CancellationToken);
        withoutParent.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RequestBody_ReadsAndWritesTheBarePrimitive()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync(
            new Uri("wrapped", UriKind.Relative),
            JsonContent.Create(new { id = 5, sku = "SKU-5", parentId = (int?)null }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        echoed.GetProperty("id").GetInt32().Should().Be(5);
        echoed.GetProperty("sku").GetString().Should().Be("SKU-5");
    }

    [Fact]
    public async Task OpenApiDocument_DescribesAWrapperAsItsPrimitive()
    {
        await using var app = await CreateHostAsync();
        using var client = app.GetTestClient();

        var document = await client.GetFromJsonAsync<JsonElement>(
            new Uri("openapi/v1.json", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var schema = document.GetProperty("components").GetProperty("schemas");

        // The route parameter.
        var routeParameter = ResolveSchema(
            schema,
            document
                .GetProperty("paths").GetProperty("/wrapped/{id}")
                .GetProperty("get").GetProperty("parameters")[0]
                .GetProperty("schema"));
        routeParameter.GetProperty("type").GetString().Should().Be("integer");
        routeParameter.GetProperty("format").GetString().Should().Be("int32");

        // And the DTO property, which is where the default generator would otherwise have produced
        // an object with a "value" member and broken every generated client.
        var orderProbe = schema.GetProperty("OrderProbe").GetProperty("properties");
        var idSchema = ResolveSchema(schema, orderProbe.GetProperty("id"));
        idSchema.GetProperty("type").GetString().Should().Be("integer");
        idSchema.TryGetProperty("properties", out _).Should().BeFalse();

        var skuSchema = ResolveSchema(schema, orderProbe.GetProperty("sku"));
        skuSchema.GetProperty("type").GetString().Should().Be("string");
    }

    /// <summary>Follows a <c>$ref</c> into <c>components/schemas</c> when the generator emitted one.</summary>
    private static JsonElement ResolveSchema(JsonElement schemas, JsonElement property)
    {
        if (!property.TryGetProperty("$ref", out var reference))
            return property;

        var name = reference.GetString()!.Split('/')[^1];
        return schemas.GetProperty(name);
    }

    /// <summary>
    /// Boots the real API pipeline with only the probe controller discovered, the way
    /// <c>OpenApiProbeHost</c> does, plus the one line a consumer writes to opt in.
    /// </summary>
    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // The opt-in. Registered here rather than through AddStronglyTypedIds because that
        // extension lives in Infrastructure, which the API test project does not reference; the
        // registration it performs is exactly this call.
        StronglyTypedIdTypeConverters.RegisterAll(typeof(ProbeOrderId).Assembly);

        builder.Services.AddAPI();

        // AddAPI registers two scoped filters that take infrastructure services this host does not
        // have. Stubbing them keeps the DI validation the test host performs at startup satisfied
        // without pulling the Infrastructure layer into an API test.
        builder.Services.AddScoped(_ => Mock.Of<ICurrentUserService>());
        builder.Services.AddScoped(_ => Mock.Of<ICacheService>());

        builder.Services.AddCommonApiVersioning();
        builder.Services.AddCommonOpenApi();

        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                    manager.FeatureProviders.RemoveAt(i);
            }

            manager.FeatureProviders.Add(new ProbeControllerFeatureProvider());
        });

        var app = builder.Build();
        app.MapControllers();
        app.MapCommonOpenApi();
        await app.StartAsync();

        return app;
    }

    private sealed class ProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
            => feature.Controllers.Add(typeof(WrappedIdProbeController).GetTypeInfo());
    }
}

/// <summary>The canonical two-line declaration, over an int key.</summary>
public readonly record struct ProbeOrderId(int Value) : IStronglyTypedId<ProbeOrderId, int>
{
    /// <summary>Wraps a primitive order key.</summary>
    public static ProbeOrderId From(int value) => new(value);
}

/// <summary>A string-backed identifier, so the schema transformer's string branch is exercised.</summary>
public readonly record struct ProbeSkuId(string Value) : IStronglyTypedId<ProbeSkuId, string>
{
    /// <summary>Wraps a stock-keeping unit.</summary>
    public static ProbeSkuId From(string value) => new(value);
}

/// <summary>The response and request contract carrying wrapped identifiers.</summary>
public sealed class OrderProbe
{
    public ProbeOrderId Id { get; set; }

    public ProbeOrderId? ParentId { get; set; }

    public ProbeSkuId Sku { get; set; }
}

/// <summary>The probe controller: one wrapper in a route segment, one in a query string, one in a body.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("wrapped")]
public sealed class WrappedIdProbeController : ControllerBase
{
    /// <summary>Echoes a wrapped identifier bound from the route segment.</summary>
    /// <param name="id">The wrapped identifier.</param>
    /// <returns>The probe contract carrying it.</returns>
    [HttpGet("{id}")]
    public ActionResult<OrderProbe> GetById(ProbeOrderId id)
        => new OrderProbe { Id = id, Sku = ProbeSkuId.From("SKU-1") };

    /// <summary>Echoes an optional wrapped identifier bound from the query string.</summary>
    /// <param name="parentId">The optional wrapped identifier.</param>
    /// <returns>The probe contract carrying it.</returns>
    [HttpGet]
    public ActionResult<OrderProbe> GetByParent([FromQuery] ProbeOrderId? parentId)
        => new OrderProbe { Id = ProbeOrderId.From(1), ParentId = parentId, Sku = ProbeSkuId.From("SKU-1") };

    /// <summary>Echoes a contract whose wrapped identifiers arrived as bare primitives in JSON.</summary>
    /// <param name="probe">The posted contract.</param>
    /// <returns>The same contract.</returns>
    [HttpPost]
    public ActionResult<OrderProbe> Post([FromBody] OrderProbe probe) => probe;
}
