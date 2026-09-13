using AwesomeAssertions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Shared.Tests.Auth.Permissions;

public sealed class PermissionRegistryTests
{
    private const string Manage = "sessions:manage";
    private const string Read = "sessions:read";

    // Role names are the app's vocabulary, not the framework's, so the test declares its own.
    private const string Manager = "Manager";
    private const string Member = "Member";
    private const string Customer = "Customer";

    [Fact]
    public void HasPermission_WhenRoleGrantsPermission_ReturnsTrue()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Manager, Manage, Read)
            .Build();

        registry.HasPermission([Manager], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_WhenRoleDoesNotGrantPermission_ReturnsFalse()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Member, Read)
            .Build();

        registry.HasPermission([Member], Manage).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_IsCaseInsensitiveOnRole()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Manager, Manage)
            .Build();

        registry.HasPermission(["manager"], Manage).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_UnionsGrantsAcrossCalls()
    {
        // Simulates two modules each contributing grants for the same role.
        var registry = new PermissionRegistryBuilder()
            .Grant(Manager, Manage)
            .Grant(Manager, Read)
            .Build();

        registry.HasPermission([Manager], Manage).Should().BeTrue();
        registry.HasPermission([Manager], Read).Should().BeTrue();
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
            .Grant(Manager, Manage)
            .Build();

        registry.HasPermission([Member, Customer], Manage).Should().BeFalse();
    }
}
