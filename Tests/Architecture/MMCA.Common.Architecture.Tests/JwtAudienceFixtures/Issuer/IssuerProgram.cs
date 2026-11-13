// Text fixture for ForwardedJwtAudienceFitnessTests (excluded from compilation in the csproj).
// The token issuer: a composed service host that selects TokenIssuer inside the call's options.
var builder = WebApplication.CreateBuilder(args);

var host = builder.AddMmcaServiceHost(new()
{
    LogFilePath = "logs/Issuer.txt",
    ModuleAssemblies = [typeof(Program).Assembly],
    Authentication = ServiceHostAuthentication.TokenIssuer,
    ConfigurePipeline = (pipeline, moduleHost) => pipeline.DiscoverModules(moduleHost),
});
