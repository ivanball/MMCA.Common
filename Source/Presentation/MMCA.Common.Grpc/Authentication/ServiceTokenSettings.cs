using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Configuration for service-to-service tokens, bound from the <c>Grpc:ServiceToken</c> section by
/// <c>AddServiceTokenClient(configuration)</c> (the calling side) and
/// <c>AddServiceTokenAuthentication(configuration)</c> (the receiving side). Both sides read the same
/// section, so the key, issuer and audience cannot drift between them within one deployment.
/// </summary>
/// <remarks>
/// The token is HS256 over a shared symmetric key. That is deliberate for east-west traffic inside
/// one deployment: there is no issuer service to call, nothing to discover, and a key rotation is a
/// configuration change on every service at once. The key is a secret; supply it from a secret
/// store, never from a committed settings file.
/// </remarks>
public sealed class ServiceTokenSettings : IValidatableObject
{
    /// <summary>Configuration section name, relative to the root.</summary>
    public const string SectionName = "Grpc:ServiceToken";

    /// <summary>The smallest key HS256 accepts: 256 bits.</summary>
    private const int MinimumSigningKeyBytes = 32;

    /// <summary>The longest lifetime, in minutes, a service token may be configured with.</summary>
    private const int MaximumLifetimeMinutes = 60;

    /// <summary>
    /// Gets the Base64-encoded HMAC key shared by every service in the deployment. Must decode to at
    /// least 32 bytes.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of this service, written as the <c>sub</c> and <c>svc</c> claims of every token
    /// it mints. Required.
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>Gets the token issuer. Defaults to <c>mmca-service</c>.</summary>
    public string Issuer { get; init; } = "mmca-service";

    /// <summary>
    /// Gets the token audience. Defaults to <c>internal-grpc</c>, which must differ from the user
    /// tokens' audience: the audience is what keeps a service token out of the user scheme.
    /// </summary>
    public string Audience { get; init; } = "internal-grpc";

    /// <summary>Gets the token lifetime in minutes, between 1 and 60. Defaults to 5.</summary>
    public int LifetimeMinutes { get; init; } = 5;

    /// <summary>
    /// Reports one result per configuration problem, so a misconfigured host fails on start with a
    /// message naming the exact defect rather than minting tokens the receiver cannot validate.
    /// </summary>
    /// <param name="validationContext">The validation context (unused: every rule here is local).</param>
    /// <returns>One result per problem; empty when the settings are usable.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(SigningKey)} is required: a Base64-encoded key of at least {MinimumSigningKeyBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                [nameof(SigningKey)]);
        }
        else if (DecodedLength(SigningKey) is not { } keyBytes)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(SigningKey)} is not valid Base64.",
                [nameof(SigningKey)]);
        }
        else if (keyBytes < MinimumSigningKeyBytes)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(SigningKey)} decodes to {keyBytes.ToString(CultureInfo.InvariantCulture)} bytes; HS256 needs at least {MinimumSigningKeyBytes.ToString(CultureInfo.InvariantCulture)}.",
                [nameof(SigningKey)]);
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(ServiceName)} is required: it becomes the sub and svc claims of every token this service mints.",
                [nameof(ServiceName)]);
        }

        if (LifetimeMinutes is < 1 or > MaximumLifetimeMinutes)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(LifetimeMinutes)} must be between 1 and {MaximumLifetimeMinutes.ToString(CultureInfo.InvariantCulture)}; it is {LifetimeMinutes.ToString(CultureInfo.InvariantCulture)}.",
                [nameof(LifetimeMinutes)]);
        }
    }

    /// <summary>The decoded key length in bytes, or <see langword="null"/> when the text is not Base64.</summary>
    private static int? DecodedLength(string base64)
    {
        var buffer = new byte[(base64.Length + 3) / 4 * 3];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? written : null;
    }
}
