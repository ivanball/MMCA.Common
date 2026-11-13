// Sample service for the AppHost-backed tier of MMCA.Common.Testing.Aspire. Every line here exists
// to give one of the fixture's Assert* helpers something real to assert against, using the framework
// helpers a consumer's own service host uses.
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MMCA.Common.API.Startup.Endpoints;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Kestrel;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Testing.Aspire.AppHostTests.SampleService;

var builder = WebApplication.CreateBuilder(args);

// One assembly, two protocol profiles, chosen by configuration, because a CLEARTEXT Kestrel endpoint
// cannot serve both. Http1AndHttp2 without TLS silently answers HTTP/1.1 only, and Kestrel says so:
// "HTTP/2 is not enabled for <address>. The endpoint is configured to use HTTP/1.1 and HTTP/2, but
// TLS is not enabled. HTTP/2 requires TLS application protocol negotiation." With no TLS there is no
// ALPN to negotiate with, so serving h2c takes HttpProtocols.Http2 ALONE. That is the profile every
// extracted MMCA service runs on cleartext, and the reason the framework ships an h2c health check
// rather than using Aspire's stock HTTP one.
//
// So the sample AppHost declares this project twice: "sample" on Http1 for the ordinary HttpClient
// probes, and "sample-h2c" on Http2 for the prior-knowledge assertion.
//
// With no HealthProbe:Port configured this call only sets endpoint defaults, so Aspire's dynamic port
// allocation keeps working.
var protocols = builder.Configuration.GetValue("SampleService:Http2Only", defaultValue: false)
    ? HttpProtocols.Http2
    : HttpProtocols.Http1;

builder.ConfigureEndpointsWithHealthProbe(protocols);

builder.AddServiceDefaults();

// Publishes /.well-known/jwks.json from whatever public key reaches the host. The AppHost forwards
// the fixture's ephemeral key through WithE2eRsaKeys(), which is the precondition an Identity
// resource silently fails without.
builder.Services.Configure<JwksSettings>(builder.Configuration.GetSection(JwksSettings.SectionName));
builder.Services.AddSingleton<IJwksProvider, RsaJwksProvider>();

// The outage scenario (the sample AppHost's SampleAppHost:Outage switch): a real SQL Server data
// source with an outbox, a RabbitMQ broker, and the framework's infrastructure health checks, so
// DependencyOutageTestBase has a database whose check is part of readiness and a broker whose check
// is not. SampleSchemaKeeper keeps the tables in place, because Aspire recreates a stopped container
// on start and a SQL Server with no data volume comes back empty.
var outage = builder.Configuration.GetValue("SampleService:Outage", defaultValue: false);
if (outage)
{
    builder.AddInfrastructureHealthChecks(requireDatabase: true);

    // The full pipeline call rather than AddApplication() alone: it also registers the decorators and
    // their fallback permission registry, which the Development host's build-time validation needs.
    builder.Services.AddMmcaApplicationPipeline();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddBrokerMessaging(builder.Configuration);
    builder.Services.AddHostedService<SampleSchemaKeeper>();
}

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapJwksEndpoint();

if (outage)
{
    // Parks one integration event in the outbox of the Default SQL Server context and answers 202.
    // During a broker outage the row stays pending until the broker is back.
    app.MapPost("/sample/outbox", async (IDbContextFactory contexts, IDataSourceResolver resolver, CancellationToken cancellationToken) =>
    {
        var context = contexts.GetDbContext(resolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName));
        context.Set<OutboxMessage>().Add(OutboxMessage.FromDomainEvent(new SampleOutageProbed()));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Results.Accepted();
    });
}

await app.RunAsync().ConfigureAwait(false);
