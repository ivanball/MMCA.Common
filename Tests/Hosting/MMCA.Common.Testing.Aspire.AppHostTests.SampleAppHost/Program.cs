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
using MMCA.Common.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var databasePath = Path.Combine(Path.GetTempPath(), "mmca-apphost-testing-sample.db");

builder.AddProject<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleService>("sample")
    .WithSqliteDataSource("Sample", databasePath)
    .WithEnvironment("Jwks__Enabled", "true")
    .WithE2eRsaKeys()
    .WithHttpHealthCheck("/alive");

await builder.Build().RunAsync().ConfigureAwait(false);
