// Text fixture for ForwardedJwtAudienceFitnessTests (excluded from compilation in the csproj).
// One composed service host on the default authentication (ForwardedJwtBearer), with its options
// object spanning several lines, plus near-misses that are not calls: a comment, a declaration and a
// <c>builder.AddMmcaServiceHost(options)</c> mention in documentation.
/// Call builder.AddMmcaServiceHost(new() { ... }) from each host.
var builder = WebApplication.CreateBuilder(args);

// var commented = builder.AddMmcaServiceHost(new() { ModuleAssemblies = [] });
var host = builder.AddMmcaServiceHost(new()
{
    LogFilePath = "logs/Fixture.txt",
    ModuleAssemblies = [typeof(Program).Assembly],
    ConfigureServices = b =>
    {
        b.Services.AddWarmupTask<FixtureWarmupTask>();
    },
    ConfigurePipeline = (pipeline, moduleHost) => pipeline.DiscoverModules(moduleHost),
});

public static class Declaration
{
    public static MmcaServiceHost AddMmcaServiceHost(WebApplicationBuilder builder) => null!;
}
