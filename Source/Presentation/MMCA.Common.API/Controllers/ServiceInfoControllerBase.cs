using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Service/version discovery actions that prove the API-versioning machinery works beyond a single
/// version (rubric §9). The same <c>/ServiceInfo</c> route is served by two API versions selected via
/// the <c>api-version</c> header: <c>1.0</c> (deprecated) returns the minimal shape; <c>2.0</c> returns
/// an evolved shape that also advertises the supported/deprecated versions. With
/// <c>ReportApiVersions = true</c> (configured in <c>AddCommonApiVersioning</c>) the responses also
/// carry <c>api-supported-versions</c> / <c>api-deprecated-versions</c> headers. Anonymous and
/// read-only; reached on the service host directly (gateways do not route this path).
/// </summary>
/// <remarks>
/// Class-level routing/versioning attributes are not reliably inherited, so the per-service sealed
/// subclass supplies them (and the service name):
/// <code>
/// [ApiController]
/// [Route("[controller]")]
/// [AllowAnonymous]
/// [ApiVersion("1.0", Deprecated = true)]
/// [ApiVersion("2.0")]
/// public sealed class ServiceInfoController : ServiceInfoControllerBase
/// {
///     protected override string ServiceName => "Conference";
/// }
/// </code>
/// </remarks>
public abstract class ServiceInfoControllerBase : ControllerBase
{
    private static readonly string[] Supported = ["1.0", "2.0"];
    private static readonly string[] Deprecated = ["1.0"];

    /// <summary>The service name advertised in the discovery payloads (e.g. "Conference").</summary>
    protected abstract string ServiceName { get; }

    /// <summary>v1.0 (deprecated): the minimal service-info shape.</summary>
    [HttpGet]
    [MapToApiVersion("1.0")]
    public ActionResult<ServiceInfoResponse> GetV1() =>
        Ok(new ServiceInfoResponse(ServiceName, "1.0"));

    /// <summary>v2.0: the evolved shape — adds the supported/deprecated version lists.</summary>
    [HttpGet]
    [MapToApiVersion("2.0")]
    public ActionResult<ServiceInfoV2Response> GetV2() =>
        Ok(new ServiceInfoV2Response(ServiceName, "2.0", Supported, Deprecated));

    /// <summary>The v1.0 service-info payload.</summary>
    public sealed record ServiceInfoResponse(string Service, string ApiVersion);

    /// <summary>The v2.0 service-info payload — a superset of <see cref="ServiceInfoResponse"/>.</summary>
    public sealed record ServiceInfoV2Response(
        string Service,
        string ApiVersion,
        IReadOnlyList<string> SupportedVersions,
        IReadOnlyList<string> DeprecatedVersions);
}
