namespace MMCA.Common.API.Startup.Auth;

/// <summary>
/// The configuration contract for the audience of the access tokens the Identity service mints.
/// Every service host that validates those tokens through <c>AddForwardedJwtBearer</c> reads the same
/// key and fails fast at startup when it is missing.
/// </summary>
/// <remarks>
/// The value itself lives in configuration only: each service's <c>appsettings.json</c> and, in
/// Azure, the deployment template. A host that silently fell back to a hard-coded audience would boot
/// with a value nobody configured and answer every request with a 401 that reads like a token problem
/// instead of a wiring problem. Pass the audience as
/// <c>JwtAudience.RequireConfigured(builder.Configuration[JwtAudience.ConfigKey])</c>, never with a
/// <c>??</c> fallback; <c>ForwardedJwtAudienceTestsBase</c> in MMCA.Common.Testing.Architecture pins
/// that shape in each consumer.
/// </remarks>
public static class JwtAudience
{
    /// <summary>Configuration key carrying the JWT audience (<c>Jwt__Audience</c> as an environment variable).</summary>
    public const string ConfigKey = "Jwt:Audience";

    /// <summary>
    /// Returns <paramref name="configuredValue"/>, or throws when it is absent or blank.
    /// </summary>
    /// <param name="configuredValue">The value read from <see cref="ConfigKey"/>.</param>
    /// <returns>The audience to hand to the JWT bearer handler.</returns>
    /// <exception cref="InvalidOperationException">The audience is not configured.</exception>
    public static string RequireConfigured(string? configuredValue) =>
        string.IsNullOrWhiteSpace(configuredValue)
            ? throw new InvalidOperationException(
                $"{ConfigKey} is not configured. Set it in the service's appsettings.json or as the Jwt__Audience environment variable.")
            : configuredValue;
}
