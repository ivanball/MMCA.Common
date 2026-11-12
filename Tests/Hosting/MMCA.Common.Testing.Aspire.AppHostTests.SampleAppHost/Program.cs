// Sample AppHost for the AppHost-backed tier of MMCA.Common.Testing.Aspire. One project resource and
// a SQLite file, so the whole stack boots with no container runtime: the point under test is the
// FIXTURE (does it start an AppHost, wait for readiness the right way, and hand the assertions a
// live application), not any particular topology.
//
// The four calls on the resource, in order:
//
// WithSqliteDataSource   SQLite has no Aspire resource of its own (it is a file), which is exactly
//                        why it is the data source here. The call injects the
//                        DataSources__Sample__SqliteConnectionString routing key the framework's
//                        multi-database resolver reads, and AssertDataSourceAsync proves the AppHost
//                        wrote the key the resolver looks for. Nothing opens the file.
// Jwks__Enabled          Turns the published key set on. Without it RsaJwksProvider answers an empty
//                        key set rather than throwing, which is the silent failure AssertJwksAsync
//                        exists to catch.
// WithE2eRsaKeys         Forwards the ephemeral RS256 keypair the fixture minted into this process's
//                        environment. Same channel that closed the 2026-09-09 nightly root cause:
//                        with no key material a host answers every request, liveness probe included,
//                        with a 500 and never turns healthy.
// WithHttpHealthCheck    Declares the LIVENESS probe as this resource's health check, so the
//                        fixture's per-resource wait means "the service can serve a request" rather
//                        than "its process started". Liveness, never readiness: a startup gate on
//                        readiness can deadlock a dependency graph.
using Microsoft.Extensions.Configuration;
using MMCA.Common.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// The outage scenario (SampleOutageAppHostFixture passes --SampleAppHost:Outage=true): ONLY a SQL
// Server container with one database, a RabbitMQ broker, and the sample service wired to both with a
// real outbox, for DependencyOutageTestBase to stop and start. No data volume on SQL Server: a per-run
// SA password would not open an old volume, and the service keeps its own schema instead. The outbox
// settings retry fast and often, so 20 attempts outlast a 90-second outage; the health report cache
// is one second, so a probe sees a state change promptly. Liveness, never readiness, is the gate.
if (builder.Configuration.GetValue("SampleAppHost:Outage", false))
{
    var sql = builder.AddSqlServer("sql");
    var sampleDb = sql.AddDatabase("sampledb");

    // The broker keeps the orchestrator's proxied endpoint, exactly as a real AppHost wires it. While
    // the broker is stopped the proxy still accepts connections and holds them; the service's broker
    // health check is bounded by HealthChecks:CheckTimeoutSeconds, so /health turns 503 anyway.
    var broker = builder.AddMessageBroker();

    builder.AddProject<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleService>("sample-outage")
        .WithSQLServerDataSource(sampleDb, "Sample")
        .WithBroker(broker)
        .WithEnvironment("SampleService__Outage", "true")
        .WithEnvironment("Outbox__PollingIntervalSeconds", "1")
        .WithEnvironment("Outbox__ProcessingDelaySeconds", "0")
        .WithEnvironment("Outbox__MaxRetries", "20")
        .WithEnvironment("Outbox__RetryBackoffBaseSeconds", "1")
        .WithEnvironment("Outbox__LeaseSeconds", "10")
        .WithEnvironment("HealthChecks__CacheSeconds", "1")
        .WithHttpHealthCheck("/alive");

    await builder.Build().RunAsync().ConfigureAwait(false);
    return;
}

var databasePath = Path.Combine(Path.GetTempPath(), "mmca-apphost-testing-sample.db");

builder.AddProject<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleService>("sample")
    .WithSqliteDataSource("Sample", databasePath)
    .WithEnvironment("Jwks__Enabled", "true")
    .WithE2eRsaKeys()
    .WithHttpHealthCheck("/alive");

// The same project on the OTHER cleartext profile: HttpProtocols.Http2 alone, which is what h2c
// prior knowledge requires and what every extracted MMCA service runs. It is a second resource
// rather than a second endpoint because a cleartext Kestrel endpoint serves one profile or the
// other: Http1AndHttp2 without TLS has no ALPN to negotiate with and answers HTTP/1.1 only.
//
// Its gate is WithH2cHealthCheck rather than the stock WithHttpHealthCheck above, for exactly the
// reason that helper exists: Aspire's stock probe sends HTTP/1.1, which an Http2-only endpoint
// answers with GOAWAY HTTP_1_1_REQUIRED, so the resource could never turn healthy and the fixture's
// wait would time out. Same /alive path, same liveness-not-readiness rule.
builder.AddProject<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleService>("sample-h2c")
    .WithEnvironment("SampleService__Http2Only", "true")
    .WithH2cHealthCheck();

await builder.Build().RunAsync().ConfigureAwait(false);
