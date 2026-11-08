namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Marks a read-only gRPC service, or a single method of one, as callable under a view-as token.
/// gRPC is always POST, so without this mark <c>ViewAsGuardMiddleware</c> refuses every view-as read
/// that fans out over gRPC. The guard reads it from the routed endpoint's metadata and treats a POST
/// to the endpoint as safe for the METHOD rule only: any other method is still refused,
/// <c>ViewAs:Enabled</c> and the grant-liveness check still apply, and the command decorator still
/// refuses every command (ADR-137).
/// </summary>
/// <remarks>
/// A host applies it deliberately, per service or per method, and only to code that performs no
/// write: it is an assertion the host makes, which nothing can verify. There is no exemption by
/// content type and none for gRPC in general. Lives in Shared so a gRPC service project can apply it
/// without taking a reference on the API package.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ViewAsReadOnlyEndpointAttribute : Attribute;
