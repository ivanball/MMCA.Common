// Sample service for the AppHost-backed tier of MMCA.Common.Testing.Aspire. Every line here exists
// to give one of the fixture's Assert* helpers something real to assert against, using the framework
// helpers a consumer's own service host uses.
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MMCA.Common.API.Startup.Endpoints;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Kestrel;
using MMCA.Common.Infrastructure.Auth;

var builder = WebApplication.CreateBuilder(args);

// Http1AndHttp2 on the cleartext listener, which is what makes BOTH assertion styles meaningful on
// one endpoint: an ordinary HttpClient probe over HTTP/1.1 for the health paths, and the h2c
// prior-knowledge probe over HTTP/2 for the protocol contract that cross-service gRPC callers need.
// With no HealthProbe:Port configured this call only sets endpoint defaults, so Aspire's dynamic
// port allocation keeps working.
builder.ConfigureEndpointsWithHealthProbe(HttpProtocols.Http1AndHttp2);

builder.AddServiceDefaults();

// Publishes /.well-known/jwks.json from whatever public key reaches the host. The AppHost forwards
// the fixture's ephemeral key through WithE2eRsaKeys(), which is the precondition an Identity
// resource silently fails without.
builder.Services.Configure<JwksSettings>(builder.Configuration.GetSection(JwksSettings.SectionName));
builder.Services.AddSingleton<IJwksProvider, RsaJwksProvider>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapJwksEndpoint();

await app.RunAsync().ConfigureAwait(false);
