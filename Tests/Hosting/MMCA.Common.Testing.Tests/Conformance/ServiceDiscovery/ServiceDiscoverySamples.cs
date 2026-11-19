namespace MMCA.Common.Testing.Tests.Conformance.ServiceDiscovery;

/// <summary>
/// Small AppHost and Bicep samples in the two shapes the consuming applications write: the ADC
/// shape (references stated on later statements, the front-door helpers handed every service) and
/// the Store shape (a reference chained onto a multi-line <c>AddDotnetProject</c> declaration, a JWKS
/// wiring beside an explicit reference, Bicep values built from <c>${app.name}</c>).
/// </summary>
internal static class ServiceDiscoverySamples
{
    /// <summary>The line <see cref="StoreShapeBicepLines"/> carries for the sales-to-identity edge.</summary>
    internal const string StoreSalesToIdentityLine =
        "            { name: 'services__identity__http__0', value: 'http://${prefix}-identity' }";

    internal static readonly string[] AdcShapeAppHostLines =
    [
        "var builder = DistributedApplication.CreateBuilder(args);",
        "var redis = builder.AddRedis(\"redis\");",
        string.Empty,
        "var notificationService = builder.AddDotnetProject(",
        "        \"notification\",",
        "        \"../Sample.Notification.Service/Sample.Notification.Service.csproj\",",
        "        options => options.LaunchProfileName = \"https\")",
        "    .WithReference(redis);",
        "var conferenceService = builder.AddDotnetProject(\"conference\", \"../Sample.Conference.Service/Sample.Conference.Service.csproj\")",
        "    .WithReference(redis);",
        "var identityService = builder.AddDotnetProject(\"identity\", \"../Sample.Identity.Service/Sample.Identity.Service.csproj\");",
        string.Empty,
        "// notificationService.WithReference(identityService) in a comment is prose, not wiring.",
        "notificationService.WithReference(conferenceService).WaitFor(conferenceService);",
        "conferenceService.WithReference(notificationService);",
        "conferenceService.WithJwksDiscovery(identityService, gateway);",
        string.Empty,
        "var gateway = builder.AddDotnetProject(\"gateway\", \"../Sample.Gateway/Sample.Gateway.csproj\")",
        "    .WithMmcaGateway(6001, notificationService, conferenceService, identityService);",
        string.Empty,
        "var ui = builder.AddDotnetProject(\"ui\", \"../Sample.UI.Web/Sample.UI.Web.csproj\", options => options.LaunchProfileName = \"https\")",
        "    .WithMmcaUi(gateway, 6002, notificationService, conferenceService, identityService);",
        "ui.WithEnvironment(\"Api__Url\", \"https://localhost:6001\");",
    ];

    internal static readonly string[] AdcShapeAppHostEdges =
    [
        "notification->conference",
        "conference->notification",
        "gateway->notification",
        "gateway->conference",
        "gateway->identity",
        "ui->gateway",
        "ui->notification",
        "ui->conference",
        "ui->identity",
    ];

    internal static readonly string[] AdcShapeBicepLines =
    [
        "param prefix string",
        string.Empty,
        "resource conferenceApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-conference'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'services__notification__grpc__0', value: 'http://${prefix}-notification:8081' }",
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
        string.Empty,
        "module alerts 'alerts.bicep' = {",
        "  name: 'alerts'",
        "  params: {",
        "    note: 'services__identity__http__0'",
        "  }",
        "}",
        string.Empty,
        "resource notificationApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-notification'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'services__conference__http__0', value: 'http://${prefix}-conference' }",
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
    ];

    internal static readonly string[] AdcShapeBicepEdges =
    [
        "conference->notification",
        "notification->conference",
    ];

    internal static readonly string[] StoreShapeAppHostLines =
    [
        "var builder = DistributedApplication.CreateBuilder(args);",
        string.Empty,
        "var catalogService = builder.AddDotnetProject(",
        "        \"catalog\",",
        "        \"../Store.Catalog.Service/Store.Catalog.Service.csproj\",",
        "        options => options.LaunchProfileName = \"https\")",
        "    .WithMmcaServiceInfrastructure(catalogDb, \"Catalog\", redis, mailDev, withBroker, ServiceEndpointProfile.Http2Only)",
        "    .WithEnvironment(\"Seeding__IncludeSampleCatalog\", \"true\");",
        "var identityService = builder.AddDotnetProject(",
        "        \"identity\",",
        "        \"../Store.Identity.Service/Store.Identity.Service.csproj\",",
        "        options => options.LaunchProfileName = \"https\")",
        "    .WithMmcaServiceInfrastructure(identityDb, \"Identity\", redis, mailDev, withBroker, ServiceEndpointProfile.Http2Only);",
        "var salesService = builder.AddDotnetProject(",
        "        \"sales\",",
        "        \"../Store.Sales.Service/Store.Sales.Service.csproj\",",
        "        options => options.LaunchProfileName = \"https\")",
        "    .WithMmcaServiceInfrastructure(",
        "        salesDb, \"Sales\", redis, mailDev, withBroker, ServiceEndpointProfile.Mixed, probeEndpointName: \"http\")",
        "    .WithReference(catalogService)",
        "    .WaitFor(catalogService)",
        "    .WaitFor(identityService);",
        "salesService.WithReference(identityService);",
        "identityService.WithReference(salesService);",
        string.Empty,
        "var gateway = builder.AddDotnetProject(\"gateway\", \"../Store.Gateway/Store.Gateway.csproj\")",
        "    .WithMmcaGateway(6001, catalogService, identityService, salesService);",
        "catalogService.WithJwksDiscovery(identityService, gateway);",
        "salesService.WithJwksDiscovery(identityService, gateway);",
        string.Empty,
        "var ui = builder.AddDotnetProject(\"ui\", \"../Store.UI.Web/Store.UI.Web.csproj\", options => options.LaunchProfileName = \"https\")",
        "    .WithMmcaUi(gateway, 6002, catalogService, identityService, salesService);",
    ];

    internal static readonly string[] StoreShapeAppHostEdges =
    [
        "sales->catalog",
        "sales->identity",
        "identity->sales",
        "gateway->catalog",
        "gateway->identity",
        "gateway->sales",
        "ui->gateway",
        "ui->catalog",
        "ui->identity",
        "ui->sales",
    ];

    internal static readonly string[] StoreShapeBicepLines =
    [
        "param prefix string",
        string.Empty,
        "resource identityApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-identity'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'services__sales__grpc__0', value: 'http://${prefix}-sales:8081' }",
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
        string.Empty,
        "resource kvCatalogUrl 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {",
        "  name: 'catalog-url'",
        "  properties: {",
        "    value: 'services__catalog__http__0'",
        "  }",
        "}",
        string.Empty,
        "resource catalogApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-catalog'",
        "}",
        string.Empty,
        "resource salesApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-sales'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'services__catalog__http__0', value: 'http://${catalogApp.name}' }",
        StoreSalesToIdentityLine,
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
        string.Empty,
        "resource gatewayApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-gateway'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'services__catalog__http__0', value: 'http://${catalogApp.name}' }",
        "            { name: 'services__identity__http__0', value: 'http://${identityApp.name}' }",
        "            { name: 'services__sales__http__0', value: 'http://${salesApp.name}' }",
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
        string.Empty,
        "resource uiApp 'Microsoft.App/containerApps@2024-03-01' = {",
        "  name: '${prefix}-ui'",
        "  properties: {",
        "    template: {",
        "      containers: [",
        "        {",
        "          env: [",
        "            { name: 'Api__ApiEndpoint', value: 'http://${gatewayApp.name}' }",
        "          ]",
        "        }",
        "      ]",
        "    }",
        "  }",
        "}",
    ];

    internal static readonly string[] StoreShapeBicepEdges =
    [
        "identity->sales",
        "sales->catalog",
        "sales->identity",
        "gateway->catalog",
        "gateway->identity",
        "gateway->sales",
    ];

    /// <summary>The Store-shape edges production does not wire, each with its reason.</summary>
    internal static readonly Dictionary<string, string> StoreShapeKnownAppHostOnlyEdges = new(StringComparer.Ordinal)
    {
        ["ui->gateway"] = "the UI reaches the gateway through Api__ApiEndpoint",
        ["ui->catalog"] = "startup ordering only",
        ["ui->identity"] = "startup ordering only",
        ["ui->sales"] = "startup ordering only",
    };

    /// <summary>Joins sample lines into one source text.</summary>
    /// <param name="lines">The sample lines.</param>
    /// <returns>The lines joined by line feeds.</returns>
    internal static string Source(IEnumerable<string> lines) => string.Join('\n', lines);
}
