using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.UI.Authorization;

/// <summary>
/// The requirement behind a <see cref="HasPagePermissionAttribute"/> policy: the caller's token carries
/// a <c>permission</c> claim equal to <paramref name="Permission"/>. Evaluated by
/// <see cref="PagePermissionAuthorizationHandler"/>.
/// </summary>
/// <param name="Permission">The permission the page requires.</param>
internal sealed record PagePermissionRequirement(string Permission) : IAuthorizationRequirement;
