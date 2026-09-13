using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Auth.Administration;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.Administration;

/// <summary>
/// The shipped role-administration service: the role universe it reports, the two lists it keeps
/// disjoint, the diff a set performs, and the two refusals that keep stored data from either
/// inventing a capability or taking over the screen that edits it.
/// </summary>
/// <remarks>
/// Everything here runs on in-memory doubles. The store's real EF implementation is exercised
/// elsewhere; what is under test is the policy this service adds on top of it, and that policy must
/// be assertable without a database.
/// </remarks>
public sealed class StoredPermissionRoleAdministrationServiceTests
{
    private const string Manage = "sessions:manage";
    private const string Read = "sessions:read";
    private const string Export = "reports:export";

    private readonly FakeGrantStore _store = new();
    private readonly FakeGrantCache _cache = new();
    private readonly Mock<IPermissionGrantCacheInvalidator> _invalidator = new();

    public StoredPermissionRoleAdministrationServiceTests() =>
        _invalidator
            .Setup(x => x.InvalidateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    // ── The role universe ──
    [Fact]
    public async Task ListRolesAsync_UnionsTheCatalog_TheConfiguredRoles_AndTheRolesWithGrants()
    {
        await _store.GrantAsync("Auditor", Read);
        var sut = CreateService(
            compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage),
            knownRoles: ["Attendee"]);

        var result = await sut.ListRolesAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(role => role.Role).Should().Equal("Attendee", "Auditor", "Organizer");
    }

    [Fact]
    public async Task ListRolesAsync_ReportsTheCompiledAndStoredListsApartAndDisjoint()
    {
        await _store.GrantAsync("Organizer", Export);
        _cache.Set("Organizer", Export);
        var sut = CreateService(
            compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage, Export));

        var result = await sut.ListRolesAsync();

        var organizer = result.Value!.Single();
        // The stored half is subtracted, so the read-only list really is only what the code grants.
        organizer.RegisteredPermissions.Should().Equal(Manage);
        organizer.StoredPermissions.Should().Equal(Export);
    }

    [Fact]
    public async Task GetCatalogAsync_ReportsTheSameRoleUniverseAndTheCompiledPermissionsOnly()
    {
        await _store.GrantAsync("Auditor", "legacy:permission");
        var sut = CreateService(
            compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage, Read),
            knownRoles: ["Attendee"]);

        var result = await sut.GetCatalogAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Roles.Should().Equal("Attendee", "Auditor", "Organizer");
        // The catalog is the compiled universe: widening it with whatever happens to be stored
        // (here, "legacy:permission") would let one typo legitimize itself.
        result.Value.Permissions.Should().Equal(Manage, Read);
    }

    // ── Reading one role ──
    [Fact]
    public async Task GetRoleAsync_ForARoleNobodyDeclaredOrGranted_IsNotFound()
    {
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage));

        var result = await sut.GetRoleAsync("Ghost");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task GetRoleAsync_ForACatalogRoleWithNoStoredGrant_ReportsItsCompiledPermissions()
    {
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage));

        var result = await sut.GetRoleAsync("Organizer");

        result.IsSuccess.Should().BeTrue();
        result.Value!.RegisteredPermissions.Should().Equal(Manage);
        result.Value.StoredPermissions.Should().BeEmpty();
    }

    // ── Setting the stored half ──
    [Fact]
    public async Task SetStoredPermissionsAsync_AddsWhatIsNewAndRemovesWhatIsGone()
    {
        await _store.GrantAsync("Organizer", Read);
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage, Read, Export));

        var result = await sut.SetStoredPermissionsAsync("Organizer", [Export], changedBy: "operator");

        result.IsSuccess.Should().BeTrue();
        result.Value!.StoredPermissions.Should().Equal(Export);
        (await _store.GetPermissionsAsync("Organizer")).Should().Equal(Export);
        _store.GrantedBy.Should().Contain("operator", "the caller is recorded on the rows a set creates");
    }

    [Fact]
    public async Task SetStoredPermissionsAsync_InvalidatesTheSnapshotOnceSoTheEditIsLiveNextRequest()
    {
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage));

        await sut.SetStoredPermissionsAsync("Organizer", [Manage]);

        _invalidator.Verify(x => x.InvalidateAsync("Organizer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetStoredPermissionsAsync_WithAnEmptySet_RemovesEveryStoredGrant()
    {
        await _store.GrantAsync("Organizer", Read);
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Read));

        var result = await sut.SetStoredPermissionsAsync("Organizer", []);

        result.IsSuccess.Should().BeTrue();
        (await _store.GetPermissionsAsync("Organizer")).Should().BeEmpty();
    }

    // ── The two refusals ──
    [Fact]
    public async Task SetStoredPermissionsAsync_RefusesToStoreManageRoles()
    {
        // Compiled in, so it is a legitimate catalog entry: the refusal is about what a stored ROW
        // may say, not about whether the permission exists.
        var sut = CreateService(
            compiled: new PermissionRegistryBuilder().Grant("Organizer", AdministrationPermissions.ManageRoles));

        var result = await sut.SetStoredPermissionsAsync("Organizer", [AdministrationPermissions.ManageRoles]);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("PermissionGrant.ManageRolesMustBeCompiled");
        result.Errors[0].Type.Should().Be(ErrorType.Validation);
        _store.Grants.Should().BeEmpty("a refused set writes nothing at all");
    }

    [Fact]
    public async Task SetStoredPermissionsAsync_RefusesAPermissionTheCatalogDoesNotContain()
    {
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage));

        var result = await sut.SetStoredPermissionsAsync("Organizer", [Manage, "sessions:manaeg"]);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("PermissionGrant.UnknownPermission");
        result.Errors[0].Message.Should().Contain("sessions:manaeg");
        _store.Grants.Should().BeEmpty("a typo must not become a row no endpoint ever checks");
    }

    [Fact]
    public async Task SetStoredPermissionsAsync_ForABlankRole_IsNotFoundRatherThanASilentWrite()
    {
        var sut = CreateService(compiled: new PermissionRegistryBuilder().Grant("Organizer", Manage));

        var result = await sut.SetStoredPermissionsAsync("   ", [Manage]);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Type.Should().Be(ErrorType.NotFound);
    }

    private StoredPermissionRoleAdministrationService CreateService(
        PermissionRegistryBuilder compiled,
        IReadOnlyList<string>? knownRoles = null)
    {
        var registry = compiled.Build();

        return new StoredPermissionRoleAdministrationService(
            registry,
            registry,
            _store,
            _cache,
            _invalidator.Object,
            Options.Create(new PermissionGrantSettings { KnownRoles = knownRoles ?? [] }));
    }

    /// <summary>
    /// The store as a list of rows: idempotent grant, idempotent revoke, and the audit label kept so
    /// a test can prove the caller reaches the row.
    /// </summary>
    private sealed class FakeGrantStore : IPermissionGrantStore
    {
        private readonly List<PermissionGrant> _grants = [];

        public IReadOnlyList<PermissionGrant> Grants => _grants;

        public List<string?> GrantedBy { get; } = [];

        public Task<IReadOnlyList<PermissionGrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PermissionGrant>>([.. _grants]);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(
            string role,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
            [
                .. _grants
                    .Where(grant => string.Equals(grant.Role, role, StringComparison.OrdinalIgnoreCase))
                    .Select(grant => grant.Permission)
                    .Order(StringComparer.Ordinal)
            ]);

        public Task<Result> GrantAsync(
            string role,
            string permission,
            string? grantedBy = null,
            CancellationToken cancellationToken = default)
        {
            GrantedBy.Add(grantedBy);

            if (!_grants.Exists(grant =>
                    string.Equals(grant.Role, role, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(grant.Permission, permission, StringComparison.Ordinal)))
            {
                _grants.Add(PermissionGrant.Create(role, permission, DateTime.UnixEpoch).Value!);
            }

            return Task.FromResult(Result.Success());
        }

        public Task<Result> RevokeAsync(string role, string permission, CancellationToken cancellationToken = default)
        {
            _grants.RemoveAll(grant =>
                string.Equals(grant.Role, role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(grant.Permission, permission, StringComparison.Ordinal));

            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>The cached snapshot, set by hand so a test can pose the "compiled AND stored" case.</summary>
    private sealed class FakeGrantCache : IPermissionGrantCache
    {
        private static readonly HashSet<string> None = [];

        private readonly Dictionary<string, HashSet<string>> _byRole = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetPermissions(string role) =>
            _byRole.TryGetValue(role, out var permissions) ? permissions : None;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Set(string role, params string[] permissions) =>
            _byRole[role] = [.. permissions];
    }
}
