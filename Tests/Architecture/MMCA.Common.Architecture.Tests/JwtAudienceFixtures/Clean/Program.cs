// Text fixture for ForwardedJwtAudienceFitnessTests (excluded from compilation in the csproj).
// The conforming shape, plus near-misses that are not calls: a comment, a declaration and a
// <c>AddForwardedJwtBearer(authority, audience)</c> mention in documentation.
/// Call services.AddForwardedJwtBearer(authority, audience) from each host.
var builder = WebApplication.CreateBuilder(args);

// services.AddForwardedJwtBearer(authority: "x", audience: "y" ?? "z");
builder.Services.AddForwardedJwtBearer(
    authority: builder.Configuration.GetRequiredJwtAuthority(),
    audience: JwtAudience.RequireConfigured(builder.Configuration[JwtAudience.ConfigKey]),
    configuration: builder.Configuration,
    environment: builder.Environment);

public static class Declaration
{
    public static IServiceCollection AddForwardedJwtBearer(IServiceCollection services) => services;
}
