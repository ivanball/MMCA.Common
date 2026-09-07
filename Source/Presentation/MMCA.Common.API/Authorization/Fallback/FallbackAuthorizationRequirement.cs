using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.API.Authorization.Fallback;

/// <summary>
/// The requirement carried by the framework's fallback authorization policy. Evaluated by
/// <see cref="FallbackAuthorizationHandler"/>, which grants it to an authenticated caller and to
/// any request whose path is listed in
/// <see cref="FallbackAuthorizationOptions.ExemptPathPrefixes"/>.
/// </summary>
/// <remarks>
/// A bare <c>RequireAuthenticatedUser()</c> policy would also gate the endpoint-routed static and
/// framework surfaces a Blazor host maps (which carry no metadata of their own), so the requirement
/// is its own type with a path-aware handler instead.
/// </remarks>
public sealed class FallbackAuthorizationRequirement : IAuthorizationRequirement;
