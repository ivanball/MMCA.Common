// Text fixture for ForwardedJwtAudienceFitnessTests (excluded from compilation in the csproj).
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddForwardedJwtBearer(
        authority: builder.Configuration.GetRequiredJwtAuthority(),
        audience: JwtAudience.RequireConfigured(builder.Configuration[JwtAudience.ConfigKey] ?? "FixtureApi"),
        configuration: builder.Configuration,
        environment: builder.Environment);
