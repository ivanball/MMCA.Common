using AwesomeAssertions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Shared.Tests.Auth;

public sealed class PermissionRegistryTests
{
    private const string Manage = "sessions:manage";
    private const string Read = "sessions:read";

    [Fact]
    public void HasPermission_WhenRoleGrantsPermission_ReturnsTrue()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(RoleNames.Organizer, Manage, Read)
            .Build();

        registry.HasPermission([RoleNames.Organizer], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_WhenRoleDoesNotGrantPermission_ReturnsFalse()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(RoleNames.Attendee, Read)
            .Build();

        registry.HasPermission([RoleNames.Attendee], Manage).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_IsCaseInsensitiveOnRole()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(RoleNames.Organizer, Manage)
            .Build();

        registry.HasPermission(["organizer"], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_UnionsGrantsAcrossCalls()
    {
        // Simulates two modules each contributing grants for the same role.
        var registry = new PermissionRegistryBuilder()
            .Grant(RoleNames.Organizer, Manage)
            .Grant(RoleNames.Organizer, Read)
            .Build();

        registry.HasPermission([RoleNames.Organizer], Manage).Should().BeTrue();
        registry.HasPermission([RoleNames.Organizer], Read).Should().BeTrue();
    }

    [Fact]
    public void GetPermissions_ForUnknownRole_ReturnsEmptySet()
    {
        var registry = new PermissionRegistryBuilder().Build();

        registry.GetPermissions("Nobody").Should().BeEmpty();
    }

    [Fact]
    public void HasPermission_WithNoMatchingRole_ReturnsFalse()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(RoleNames.Organizer, Manage)
            .Build();

        registry.HasPermission([RoleNames.Attendee, RoleNames.Customer], Manage).Should().BeFalse();
    }
}
