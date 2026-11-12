// Text fixture for ForwardedJwtAudienceFitnessTests (excluded from compilation in the csproj).
// The token issuer with its options built in a variable: TokenIssuer is selected outside the call.
var builder = WebApplication.CreateBuilder(args);

var options = new MmcaServiceHostOptions
{
    ModuleAssemblies = [typeof(Program).Assembly],
    Authentication = ServiceHostAuthentication.TokenIssuer,
    ConfigurePipeline = (pipeline, moduleHost) => pipeline.DiscoverModules(moduleHost),
};

var host = builder.AddMmcaServiceHost(options);
