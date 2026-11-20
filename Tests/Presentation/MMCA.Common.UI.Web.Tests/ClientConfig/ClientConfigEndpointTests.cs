using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.ClientConfig;

namespace MMCA.Common.UI.Web.Tests.ClientConfig;

/// <summary>
/// Tests for <c>MapClientConfigEndpoint</c>: the served document (the framework <c>api</c> and
/// <c>modules</c> sections plus a host's extras, camelCased as an anonymous-object payload would be), the fail-closed path
/// when <c>Api:WasmApiEndpoint</c> is missing (never a fallback to the service-discovery name), and
/// the anonymous, undocumented endpoint metadata the WASM boot depends on.
/// </summary>
public sealed class ClientConfigEndpointTests
{
    private const string Gateway = "https://gateway.example.com";

    [Fact]
    public async Task ServesTheWasmApiEndpoint()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        document.RootElement.GetProperty("api").GetProperty("apiEndpoint").GetString().Should().Be(Gateway);
        document.RootElement.EnumerateObject().Select(section => section.Name).Should().Equal("api", "modules");
        document.RootElement.GetProperty("modules").EnumerateObject().Should().BeEmpty("a head with no Modules section names no module");
    }

    [Fact]
    public async Task ServesTheHostExtrasBesideTheApiSection()
    {
        await using var app = CreateApp(Gateway, ("Support:Email", "help@example.com"));
        app.MapClientConfigEndpoint(config => config
            .Add("OAuth", new { GoogleEnabled = true, GitHubEnabled = false })
            .Add("Support", new { Email = config.Configuration["Support:Email"] }));

        using var document = await InvokeAsync(app);

        var root = document.RootElement;
        root.GetProperty("api").GetProperty("apiEndpoint").GetString().Should().Be(Gateway);
        root.GetProperty("oAuth").GetProperty("googleEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("oAuth").GetProperty("gitHubEnabled").GetBoolean().Should().BeFalse();
        root.GetProperty("support").GetProperty("email").GetString().Should().Be("help@example.com");
    }

    [Fact]
    public async Task WithoutAWasmApiEndpoint_FailsClosedInsteadOfServingTheDiscoveryName()
    {
        await using var app = CreateApp(wasmApiEndpoint: null);
        app.MapClientConfigEndpoint();

        var act = () => InvokeAsync(app);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Api:WasmApiEndpoint");
    }

    // A deployment placeholder, a relative path, another scheme or a service-discovery name is not a
    // base address a browser can call: the endpoint fails closed exactly as it does for a missing key.
    [Theory]
    [InlineData("SET-VIA-ENVIRONMENT-VARIABLE")]
    [InlineData("/relative/path")]
    [InlineData("ftp://gateway.example.com")]
    [InlineData("https+http://gateway")]
    public async Task ANonHttpWasmApiEndpoint_FailsClosedNamingTheKeyAndTheRequiredShape(string wasmApiEndpoint)
    {
        await using var app = CreateApp(wasmApiEndpoint);
        app.MapClientConfigEndpoint();

        var act = () => InvokeAsync(app);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Api:WasmApiEndpoint").And.Contain("absolute http(s) URL");
    }

    [Fact]
    public async Task AnExtraNamedApi_IsRefused()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint(config => config.Add("api", new { ApiEndpoint = "https://elsewhere" }));

        var act = () => InvokeAsync(app);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ServesTheHeadsModuleFlags_AndNothingElseOfEachModule()
    {
        await using var app = CreateApp(
            Gateway,
            ("Modules:Identity:Enabled", "true"),
            ("Modules:Catalog:Enabled", "false"),
            ("Modules:Sales:RemoteDependencies:0", "Identity"));
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        var modules = document.RootElement.GetProperty("modules");
        modules.EnumerateObject().Select(module => module.Name).Should().BeEquivalentTo("Identity", "Catalog", "Sales");
        modules.GetProperty("Identity").GetProperty("enabled").GetBoolean().Should().BeTrue();
        modules.GetProperty("Catalog").GetProperty("enabled").GetBoolean().Should().BeFalse();
        modules.GetProperty("Sales").GetProperty("enabled").GetBoolean().Should().BeTrue("Enabled defaults to true once the key exists");
        modules.GetProperty("Sales").EnumerateObject().Should().ContainSingle(
            "only the resolved switch is forwarded, never a module's other settings")
            .Which.Name.Should().Be("enabled");
    }

    [Fact]
    public async Task AConfigurationBuiltFromTheDocument_EnablesExactlyTheModulesTheHeadDoes()
    {
        (string Key, string Value)[] headModules =
        [
            ("Modules:Identity:Enabled", "true"),
            ("Modules:Catalog:Enabled", "false"),
            ("Modules:Sales:RemoteDependencies:0", "Identity"),
        ];
        await using var app = CreateApp(Gateway, headModules);
        app.MapClientConfigEndpoint();
        var head = app.Services.GetRequiredService<IConfiguration>();

        using var document = await InvokeAsync(app);
        await using var served = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document.RootElement));
        var client = new ConfigurationBuilder().AddJsonStream(served).Build();

        foreach (var module in new[] { "Identity", "identity", "Catalog", "Sales", "Notification" })
        {
            UIModuleConfiguration.IsModuleEnabled(client, module).Should().Be(
                UIModuleConfiguration.IsModuleEnabled(head, module),
                $"the WASM client must register {module} exactly when the server head does");
        }
    }

    [Fact]
    public async Task AnExtraNamedModules_IsRefused()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint(config => config.Add("modules", new { Identity = new { Enabled = true } }));

        var act = () => InvokeAsync(app);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheEndpoint_IsAnonymousAndExcludedFromTheApiDescription()
    {
        await using var app = CreateApp(Gateway);
        app.MapClientConfigEndpoint();

        var endpoint = FindEndpoint(app);

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription.Should().BeTrue();
        endpoint.RoutePattern.RawText.Should().Be(ClientConfigEndpointExtensions.ClientConfigPath);
    }

    [Fact]
    public async Task ForwardsTheCommonUiSections_OnlyTheKeysTheServerSets()
    {
        await using var app = CreateApp(
            Gateway,
            ("Layout:BrandName", "AtlDevCon"),
            ("Layout:FooterText", "(c) Example"),
            ("Registration:CollectAddress", "false"),
            ("SignIn:OfferSignInLink", "true"),
            ("SignIn:SignInLinkLandingPath", "/conference"),
            ("Legal:TermsUrl", "https://example.com/terms"));
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        var root = document.RootElement;
        root.EnumerateObject().Select(section => section.Name).Should().Equal("api", "modules", "layout", "registration", "signIn", "legal");
        PropertyNames(root.GetProperty("layout")).Should().BeEquivalentTo("brandName", "footerText");
        root.GetProperty("layout").GetProperty("brandName").GetString().Should().Be("AtlDevCon");
        root.GetProperty("layout").GetProperty("footerText").GetString().Should().Be("(c) Example");
        root.GetProperty("registration").GetProperty("collectAddress").GetBoolean().Should().BeFalse();
        PropertyNames(root.GetProperty("signIn")).Should().BeEquivalentTo("offerSignInLink", "signInLinkLandingPath");
        root.GetProperty("signIn").GetProperty("offerSignInLink").GetBoolean().Should().BeTrue();
        root.GetProperty("signIn").GetProperty("signInLinkLandingPath").GetString().Should().Be("/conference");
        PropertyNames(root.GetProperty("legal")).Should().BeEquivalentTo(
            ["termsUrl"],
            "a key the server leaves unset is not written, so the client's own default holds");
    }

    [Fact]
    public async Task ASectionTheServerDoesNotConfigure_IsLeftOut()
    {
        await using var app = CreateApp(Gateway, ("Layout:BrandName", "AtlDevCon"));
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        document.RootElement.EnumerateObject().Select(section => section.Name).Should().Equal("api", "modules", "layout");
    }

    [Fact]
    public async Task KeysTheSettingsClassDoesNotDeclare_AreNeverForwarded()
    {
        // The served document is anonymous: a server-only key under a forwarded section name (the API
        // host's terms version, or anything secret an operator put there) must not reach the browser,
        // and neither must a computed member of the settings class.
        await using var app = CreateApp(
            Gateway,
            ("Legal:PrivacyUrl", "https://example.com/privacy"),
            ("Legal:CurrentTermsVersion", "7"),
            ("Legal:ApiKey", "do-not-serve"),
            ("Legal:HasFooterLinks", "false"),
            ("SignIn:ClientSecret", "do-not-serve"));
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);

        var root = document.RootElement;
        PropertyNames(root.GetProperty("legal")).Should().BeEquivalentTo("privacyUrl");
        root.TryGetProperty("signIn", out _).Should().BeFalse("no declared SignIn key is set, so the section is left out");
        root.GetRawText().Should().NotContain("do-not-serve");
    }

    [Fact]
    public async Task EveryDeclaredProperty_RoundTripsIntoTheClientsBinding()
    {
        // What the server binds is what the WASM client binds after loading the document into its
        // configuration (MmcaClientConfigBootstrap + AddJsonStream), for every settable property of
        // the four classes: a property added to one of them is forwarded with no change here.
        await using var app = CreateApp(
            Gateway,
            ("Layout:BrandName", "AtlDevCon"),
            ("Layout:FooterText", "(c) Example"),
            ("Layout:BrandLogoUrl", "/img/logo.svg"),
            ("Layout:SessionsNavRequiredRole", "Admin"),
            ("Layout:StampColorModeOnDocument", "true"),
            ("Registration:CollectAddress", "false"),
            ("SignIn:OfferSignInLink", "true"),
            ("SignIn:OfferPasskeys", "true"),
            ("SignIn:SignInLinkLandingPath", "/conference"),
            ("Legal:TermsUrl", "https://example.com/terms"),
            ("Legal:PrivacyUrl", "https://example.com/privacy"),
            ("Legal:CodeOfConductUrl", "https://example.com/conduct"),
            ("Legal:DeleteAccountUrl", "https://example.com/delete"));
        app.MapClientConfigEndpoint();

        using var document = await InvokeAsync(app);
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
        var client = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var server = app.Services.GetRequiredService<IConfiguration>();

        foreach (var (sectionName, settingsType) in ClientConfigUiSections.Forwarded)
        {
            var properties = ClientConfigUiSections.DeclaredSettableProperties(settingsType).ToList();
            properties.Should().NotBeEmpty();
            var expected = server.GetSection(sectionName).Get(settingsType)!;
            var actual = client.GetSection(sectionName).Get(settingsType)!;
            foreach (var property in properties)
            {
                server.GetSection(sectionName).GetSection(property.Name).Value.Should().NotBeNull(
                    $"the round trip must set every declared property; add {sectionName}:{property.Name} to this test");
                property.GetValue(actual).Should().Be(property.GetValue(expected), $"{sectionName}:{property.Name} must reach the client unchanged");
            }
        }
    }

    [Fact]
    public async Task AHostThatStillAddsLegalAndSignIn_KeepsServingItsOwnValues()
    {
        // The ADC shape before its version bump: the head forwards Legal and SignIn field by field.
        await using var app = CreateApp(
            Gateway,
            ("Legal:TermsUrl", "https://example.com/terms"),
            ("SignIn:OfferSignInLink", "true"));
        app.MapClientConfigEndpoint(config => config
            .Add("Legal", new
            {
                TermsUrl = config.Configuration["Legal:TermsUrl"],
                PrivacyUrl = config.Configuration["Legal:PrivacyUrl"],
                CodeOfConductUrl = config.Configuration["Legal:CodeOfConductUrl"],
                DeleteAccountUrl = config.Configuration["Legal:DeleteAccountUrl"],
            })
            .Add("SignIn", new
            {
                OfferSignInLink = config.Configuration.GetValue<bool>("SignIn:OfferSignInLink"),
                SignInLinkLandingPath = config.Configuration["SignIn:SignInLinkLandingPath"],
                OfferPasskeys = config.Configuration.GetValue<bool>("SignIn:OfferPasskeys"),
            }));

        using var document = await InvokeAsync(app);

        var root = document.RootElement;
        root.GetProperty("legal").GetProperty("termsUrl").GetString().Should().Be("https://example.com/terms");
        root.GetProperty("signIn").GetProperty("offerSignInLink").GetBoolean().Should().BeTrue();
        root.GetProperty("signIn").GetProperty("offerPasskeys").GetBoolean().Should().BeFalse();
        root.EnumerateObject().Count(section => section.NameEquals("legal")).Should().Be(1);
    }

    private static IEnumerable<string> PropertyNames(JsonElement section) =>
        section.EnumerateObject().Select(property => property.Name);

    private static WebApplication CreateApp(string? wasmApiEndpoint, params (string Key, string Value)[] extra)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Api:ApiEndpoint"] = "https+http://gateway",
            ["Api:WasmApiEndpoint"] = wasmApiEndpoint,
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOptions<ApiSettings>().Bind(builder.Configuration.GetSection(ApiSettings.SectionName));
        return builder.Build();
    }

    private static RouteEndpoint FindEndpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, ClientConfigEndpointExtensions.ClientConfigPath, StringComparison.Ordinal));

    private static async Task<JsonDocument> InvokeAsync(WebApplication app)
    {
        var endpoint = FindEndpoint(app);
        await using var scope = app.Services.CreateAsyncScope();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = body;

        await endpoint.RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Position = 0;
        return await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
    }
}
