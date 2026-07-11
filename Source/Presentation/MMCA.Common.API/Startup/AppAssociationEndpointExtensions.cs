using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps the app-association documents that let a mobile OS hand https links on this host to the
/// installed native app: Android Digital Asset Links at <c>/.well-known/assetlinks.json</c>
/// (verified against the app's signing certificate) and the Apple App Site Association document
/// (the <c>webcredentials</c> + <c>applinks</c> envelope consumed by Universal Links). Values come
/// from <see cref="AppAssociationOptions"/>. Both endpoints are anonymous by design: the OS and
/// Apple's CDN fetch them without credentials.
/// </summary>
public static class AppAssociationEndpointExtensions
{
    /// <summary>Digital Asset Links path per RFC 8615 (well-known URIs).</summary>
    public const string AssetLinksPath = "/.well-known/assetlinks.json";

    /// <summary>
    /// Apple App Site Association path. No file extension: Apple requires this exact path; the
    /// content type must still be JSON.
    /// </summary>
    public const string AppleAppSiteAssociationPath = "/.well-known/apple-app-site-association";

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the two well-known app-association endpoints from <paramref name="options"/>. Both
        /// documents are built once at map time (they are static for the process lifetime), served as
        /// JSON, anonymous, and excluded from the OpenAPI description.
        /// </summary>
        /// <param name="options">The app identifiers and applinks URL patterns to serialize.</param>
        /// <returns>The endpoint route builder for chaining.</returns>
        public IEndpointRouteBuilder MapAppAssociationEndpoints(AppAssociationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var assetLinks = BuildAssetLinks(options);
            var appleAppSiteAssociation = BuildAppleAppSiteAssociation(options);

            endpoints.MapGet(AssetLinksPath, () => Results.Json(assetLinks))
                .AllowAnonymous()
                .ExcludeFromDescription();

            endpoints.MapGet(AppleAppSiteAssociationPath, () => Results.Json(appleAppSiteAssociation))
                .AllowAnonymous()
                .ExcludeFromDescription();

            return endpoints;
        }
    }

    private static Dictionary<string, object>[] BuildAssetLinks(AppAssociationOptions options) =>
    [
        new Dictionary<string, object>
        {
            ["relation"] = new[] { "delegate_permission/common.handle_all_urls" },
            ["target"] = new Dictionary<string, object>
            {
                ["namespace"] = "android_app",
                ["package_name"] = options.AndroidPackageName,
                ["sha256_cert_fingerprints"] = options.AndroidCertFingerprints,
            },
        },
    ];

    private static Dictionary<string, object> BuildAppleAppSiteAssociation(AppAssociationOptions options) =>
        new()
        {
            ["applinks"] = new Dictionary<string, object>
            {
                ["details"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["appIDs"] = new[] { options.AppleAppId },
                        ["components"] = options.AppleAppLinkComponents
                            .Select(pattern => new Dictionary<string, string> { ["/"] = pattern })
                            .ToArray(),
                    },
                },
            },
            ["webcredentials"] = new Dictionary<string, object>
            {
                ["apps"] = new[] { options.AppleAppId },
            },
        };
}
