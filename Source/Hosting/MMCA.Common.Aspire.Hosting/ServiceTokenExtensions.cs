using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// AppHost helpers for the local gRPC service-token signing key (ADR-061): one generated, persisted
/// secret parameter every service-token minter and validator shares, handed to each service under the
/// key <c>ServiceTokenSettings</c> binds.
/// <para>
/// This package does not reference MMCA.Common.Grpc, so the section and property names are internal
/// mirrors of <c>ServiceTokenSettings.SectionName</c> and <c>ServiceTokenSettings.SigningKey</c>,
/// cross-asserted by the hosting tests.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ServiceTokenExtensions
{
    /// <summary>
    /// The default parameter name. It is the user-secrets slot existing AppHosts already persist the
    /// key under, so adopting the helper regenerates no developer key.
    /// </summary>
    public const string DefaultSigningKeyParameterName = "grpc-service-token-signing-key";

    /// <summary>Mirror of <c>ServiceTokenSettings.SectionName</c>.</summary>
    internal const string ServiceTokenSection = "Grpc:ServiceToken";

    /// <summary>Mirror of <c>nameof(ServiceTokenSettings.SigningKey)</c>.</summary>
    internal const string SigningKeyProperty = "SigningKey";

    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the service-token signing key as a secret parameter whose value, when configuration
        /// supplies none, is generated (64 letters and digits, valid Base64 decoding to 48 bytes, above
        /// the 32-byte floor <c>ServiceTokenSettings</c> enforces) and persisted to the AppHost's user
        /// secrets, so it survives restarts.
        /// </summary>
        /// <param name="name">The parameter name (defaults to <see cref="DefaultSigningKeyParameterName"/>).</param>
        /// <returns>The parameter resource builder.</returns>
        public IResourceBuilder<ParameterResource> AddServiceTokenSigningKey(string name = DefaultSigningKeyParameterName)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            return builder.AddParameter(
                name,
                new GenerateParameterDefault { MinLength = 64, Special = false },
                secret: true,
                persist: true);
        }
    }

    extension(IResourceBuilder<ProjectResource> service)
    {
        /// <summary>
        /// Hands the signing key to one service as <c>Grpc__ServiceToken__SigningKey</c> (the parameter
        /// itself, not a copy of its value).
        /// </summary>
        /// <param name="signingKey">The parameter <c>AddServiceTokenSigningKey</c> returned.</param>
        /// <returns>The same service resource builder, for chaining.</returns>
        public IResourceBuilder<ProjectResource> WithServiceTokenSigningKey(IResourceBuilder<ParameterResource> signingKey)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(signingKey);

            return service.WithEnvironment(
                IdentityLinkExtensions.EnvironmentKey(ServiceTokenSection, SigningKeyProperty),
                signingKey);
        }
    }
}
