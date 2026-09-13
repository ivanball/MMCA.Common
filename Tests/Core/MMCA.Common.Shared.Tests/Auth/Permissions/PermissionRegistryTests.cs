using AwesomeAssertions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Shared.Tests.Auth.Permissions;

public sealed class PermissionRegistryTests
{
    private const string Manage = "sessions:manage";
    private const string Read = "sessions:read";

    // Role names are the app's vocabulary, not the framework's, so the test declares its own.
    private const string Organizer = "Organizer";
    private const string Attendee = "Attendee";
    private const string Customer = "Customer";

    [Fact]
    public void HasPermission_WhenRoleGrantsPermission_ReturnsTrue()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage, Read)
            .Build();

        registry.HasPermission([Organizer], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_WhenRoleDoesNotGrantPermission_ReturnsFalse()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Attendee, Read)
            .Build();

        registry.HasPermission([Attendee], Manage).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_IsCaseInsensitiveOnRole()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage)
            .Build();

        registry.HasPermission(["organizer"], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_UnionsGrantsAcrossCalls()
    {
        // Simulates two modules each contributing grants for the same role.
        var registry = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage)
            .Grant(Organizer, Read)
            .Build();

        registry.HasPermission([Organizer], Manage).Should().BeTrue();
        registry.HasPermission([Organizer], Read).Should().BeTrue();
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
            .Grant(Organizer, Manage)
            .Build();

        registry.HasPermission([Attendee, Customer], Manage).Should().BeFalse();
    }
}
