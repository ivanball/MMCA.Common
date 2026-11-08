using Microsoft.AspNetCore.Authorization;
using MMCA.Common.API.Authorization;
using MMCA.Common.Mcp;
using MMCA.Common.UI.Authorization;

namespace MMCA.Common.Architecture.Tests.PermissionCoverageFixtures;

/// <summary>
/// The permissions the fixtures below name, one per way a surface can name one. Nothing grants them,
/// so the permission coverage rule must report each, naming the shape and the member.
/// </summary>
public static class FixturePermissions
{
    /// <summary>Named by a controller action's <c>[HasPermission]</c>.</summary>
    public const string Action = "fixture:action";

    /// <summary>Named by a page's <c>[HasPagePermission]</c>.</summary>
    public const string Page = "fixture:page";

    /// <summary>Named by an MCP tool's <c>[McpToolPermission]</c>.</summary>
    public const string Tool = "fixture:tool";

    /// <summary>Named by a hand-written <c>[Authorize(Policy = "perm:...")]</c>.</summary>
    public const string Policy = "fixture:policy";
}

/// <summary>A controller whose action is gated on a permission (method level).</summary>
public sealed class FixtureGatedController
{
    /// <summary>The gated action.</summary>
    [HasPermission(FixturePermissions.Action)]
    public void Act()
    {
        // Only the attribute matters: the rule reads it by reflection and never calls the method.
    }
}

/// <summary>A page gated on a permission (type level).</summary>
[HasPagePermission(FixturePermissions.Page)]
public sealed class FixtureGatedPage;

/// <summary>An MCP tool type whose tool is gated on a permission.</summary>
public static class FixtureGatedTools
{
    /// <summary>The gated tool.</summary>
    [McpToolPermission(FixturePermissions.Tool)]
    public static void Tool()
    {
        // Only the attribute matters: the rule reads it by reflection and never calls the method.
    }
}

/// <summary>A page using the permission policy name by hand rather than through the attribute.</summary>
[Authorize(Policy = "perm:" + FixturePermissions.Policy)]
public sealed class FixtureHandWrittenPolicyPage;

/// <summary>A page gated on a named policy that is not a permission: never reported.</summary>
[Authorize(Policy = "fixture-named-policy")]
public sealed class FixtureNamedPolicyPage;
