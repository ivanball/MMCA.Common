using MMCA.Common.API.Controllers;
using MMCA.Common.Architecture.Tests.PermissionCoverageFixtures;
using MMCA.Common.Mcp;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Testing.Architecture;
using MMCA.Common.UI;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Runs the permission coverage rule over MMCA.Common's own API, UI and MCP packages through
/// <see cref="PermissionCoverageTestsBase"/>, the way the consumers subclass it. Common grants no role
/// anything, so its declared set is the framework's own permission constants: a framework surface
/// naming a permission no constant declares fails here.
/// </summary>
public sealed class PermissionCoverageTests : PermissionCoverageTestsBase
{
    protected override IReadOnlyCollection<Assembly> TargetAssemblies { get; } =
    [
        typeof(ApiControllerBase).Assembly,
        typeof(UISharedAssemblyReference).Assembly,
        typeof(McpToolPermissionAttribute).Assembly,
    ];

    protected override IReadOnlyCollection<string> DeclaredPermissions { get; } =
    [
        AdministrationPermissions.ManageUsers,
        AdministrationPermissions.ManageRoles,
        AdministrationPermissions.ViewAsUsers,
        NotificationPermissions.Manage,
    ];

    // The admin and notification controller bases name five permissions between them.
    protected override int MinimumNamedPermissions => 5;
}

/// <summary>
/// Adversarial coverage for the rule, pointed at THIS test assembly, whose fixtures name one ungranted
/// permission per shape. Each failure must name the permission, the shape and the member.
/// </summary>
public sealed class PermissionCoverageRuleTests
{
    private static readonly Assembly[] Fixtures = [typeof(FixtureGatedController).Assembly];

    private static readonly string[] AllFixturePermissions =
    [
        FixturePermissions.Action,
        FixturePermissions.Page,
        FixturePermissions.Tool,
        FixturePermissions.Policy,
    ];

    [Fact]
    public void UngrantedPermissions_FailWithOneLinePerShape()
    {
        var act = () => ArchitectureRules.NamedPermissionsAreGranted(Fixtures, []);

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Split('\n').Count(line => line.StartsWith("  - '", StringComparison.Ordinal))
            .Should().Be(4, "one fixture per shape names one ungranted permission");
        message.Should().Contain($"'{FixturePermissions.Action}' named by [HasPermission] on {typeof(FixtureGatedController).FullName}.{nameof(FixtureGatedController.Act)}");
        message.Should().Contain($"'{FixturePermissions.Page}' named by [HasPagePermission] on {typeof(FixtureGatedPage).FullName}");
        message.Should().Contain($"'{FixturePermissions.Tool}' named by [McpToolPermission] on {typeof(FixtureGatedTools).FullName}.{nameof(FixtureGatedTools.Tool)}");
        message.Should().Contain($"'{FixturePermissions.Policy}' named by [Authorize(Policy)] on {typeof(FixtureHandWrittenPolicyPage).FullName}");
        message.Should().NotContain("fixture-named-policy", "a named policy that is not a permission is not a permission reference");
    }

    [Fact]
    public void FullyGrantedPermissions_Pass() =>
        FluentActions.Invoking(() => ArchitectureRules.NamedPermissionsAreGranted(Fixtures, AllFixturePermissions))
            .Should().NotThrow();

    [Fact]
    public void AnAdditionalReferencedPermission_IsChecked()
    {
        var act = () => ArchitectureRules.NamedPermissionsAreGranted(Fixtures, AllFixturePermissions, ["fixture:menu"]);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("'fixture:menu' named by AdditionalReferencedPermissions");
    }

    [Fact]
    public void TheMatchIsCaseSensitive_LikeThePermissionRegistry()
    {
        string[] upperCased = [.. AllFixturePermissions.Select(p => p.ToUpperInvariant())];

        var act = () => ArchitectureRules.NamedPermissionsAreGranted(Fixtures, upperCased);

        act.Should().Throw<Exception>("the registry compares permissions ordinally, so a casing slip is a real miss");
    }

    [Fact]
    public void TheScan_CountsEveryShape() =>
        ArchitectureRules.CountNamedPermissions(Fixtures).Should().BeGreaterThanOrEqualTo(4);
}
