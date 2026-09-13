using AwesomeAssertions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Shared.Tests.Auth.Permissions;

/// <summary>
/// The catalog is the closed list an administration screen renders and the closed list a stored
/// grant may pick from, so what the builder was given has to come back out whole, ordered and
/// de-duplicated. It is a projection of the very map the registry answers from, which is what stops
/// the two from drifting.
/// </summary>
public sealed class PermissionCatalogTests
{
    private const string Manage = "sessions:manage";
    private const string Read = "sessions:read";
    private const string Export = "reports:export";

    // Role names are the app's vocabulary, not the framework's, so the test declares its own.
    private const string Organizer = "Organizer";
    private const string Attendee = "Attendee";
    private const string Admin = "Admin";

    [Fact]
    public void Roles_ReportsEveryRoleTheBuilderWasGiven_Ordered()
    {
        IPermissionCatalog catalog = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage)
            .Grant(Admin, Export)
            .Build();

        catalog.Roles.Should().Equal(Admin, Organizer);
    }

    [Fact]
    public void Permissions_ReportsTheUnionAcrossRoles_OrderedAndDeduplicated()
    {
        IPermissionCatalog catalog = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage, Read)
            .Grant(Admin, Read, Export)
            .Build();

        catalog.Permissions.Should().Equal(Export, Manage, Read);
    }

    [Fact]
    public void ARoleGrantedNothing_IsStillListed()
    {
        IPermissionCatalog catalog = new PermissionRegistryBuilder()
            .Grant(Attendee)
            .Build();

        // A role with no compiled permission is still a role an operator has to be able to widen.
        catalog.Roles.Should().Equal(Attendee);
        catalog.Permissions.Should().BeEmpty();
    }

    /// <summary>
    /// Role keys are case-insensitive everywhere else (<see cref="IPermissionRegistry"/> says so), so
    /// two spellings of one role must not show up as two rows on the administration screen.
    /// </summary>
    [Fact]
    public void RolesDifferingOnlyInCase_AreOneEntry()
    {
        IPermissionCatalog catalog = new PermissionRegistryBuilder()
            .Grant("Organizer", Manage)
            .Grant("ORGANIZER", Read)
            .Build();

        catalog.Roles.Should().ContainSingle();
        catalog.Permissions.Should().Equal(Manage, Read);
    }

    [Fact]
    public void AnEmptyBuilder_YieldsAnEmptyCatalog()
    {
        IPermissionCatalog catalog = new PermissionRegistryBuilder().Build();

        catalog.Roles.Should().BeEmpty();
        catalog.Permissions.Should().BeEmpty();
    }

    /// <summary>
    /// The catalog and the authorization answer come from the ONE built instance, so a permission
    /// the catalog offers is a permission the registry really grants.
    /// </summary>
    [Fact]
    public void TheCatalogAndTheRegistry_AreTheSameObject()
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(Organizer, Manage)
            .Build();

        registry.Should().BeAssignableTo<IPermissionCatalog>();
        ((IPermissionCatalog)registry).Permissions.Should().Equal(Manage);
        registry.HasPermission([Organizer], Manage).Should().BeTrue();
    }
}
