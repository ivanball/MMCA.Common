using AwesomeAssertions;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Verifies how the compiled registry and the stored grants combine: the two layers union, the
/// compiled layer is asked first, a stored row can only ever add a capability, and an invalidated
/// cache stops granting immediately.
/// </summary>
public sealed class LayeredPermissionRegistryTests
{
    private static readonly string[] AdminRole = ["Admin"];
    private static readonly string[] CustomerRole = ["Customer"];
    private static readonly string[] BothRoles = ["Customer", "Admin"];
    private static readonly string[] CompiledAndStored = ["orders:write", "reports:read"];
    private static readonly string[] CompiledOnly = ["orders:write"];

    // ── Precedence and union ──
    [Fact]
    public void HasPermission_WhenTheCompiledRegistryGrantsIt_IsAllowedWithoutConsultingTheStore()
    {
        var cache = new FakeGrantCache();
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), cache);

        sut.HasPermission(AdminRole, "orders:write").Should().BeTrue();
        cache.Reads.Should().BeEmpty("the compiled layer is a frozen set lookup and the common answer");
    }

    [Fact]
    public void HasPermission_WhenOnlyAStoredGrantCoversIt_IsAllowed()
    {
        var cache = new FakeGrantCache();
        cache.Grant("Customer", "orders:read");
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), cache);

        sut.HasPermission(CustomerRole, "orders:read").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_WhenNeitherLayerCoversIt_IsDenied()
    {
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), new FakeGrantCache());

        sut.HasPermission(CustomerRole, "orders:write").Should().BeFalse();
    }

    [Fact]
    public void HasPermission_ChecksEveryRoleTheCallerHolds()
    {
        var cache = new FakeGrantCache();
        cache.Grant("Admin", "reports:read");
        var sut = new LayeredPermissionRegistry(Compiled(), cache);

        sut.HasPermission(BothRoles, "reports:read").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_WithNoRoles_IsDenied()
    {
        var cache = new FakeGrantCache();
        cache.Grant("Admin", "reports:read");
        var sut = new LayeredPermissionRegistry(Compiled(), cache);

        sut.HasPermission([], "reports:read").Should().BeFalse();
    }

    // ── A stored row can only add ──
    [Fact]
    public void GetPermissions_ReturnsBothLayersUnioned()
    {
        var cache = new FakeGrantCache();
        cache.Grant("Admin", "reports:read");
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), cache);

        sut.GetPermissions("Admin").Should().BeEquivalentTo(CompiledAndStored);
    }

    [Fact]
    public void GetPermissions_WithNoStoredGrants_ReturnsTheCompiledSetUnchanged()
    {
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), new FakeGrantCache());

        sut.GetPermissions("Admin").Should().BeEquivalentTo(CompiledOnly);
    }

    [Fact]
    public void HasPermission_WhenTheStoredGrantsAreEmptied_StillHonoursTheCompiledLayer()
    {
        // There is no stored denial: removing every row cannot take away what the code grants.
        var cache = new FakeGrantCache();
        cache.Grant("Admin", "orders:write");
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), cache);

        cache.Clear();

        sut.HasPermission(AdminRole, "orders:write").Should().BeTrue();
    }

    // ── Invalidation ──
    [Fact]
    public void HasPermission_AfterTheCachedGrantIsInvalidated_IsDeniedOnTheNextCall()
    {
        var cache = new FakeGrantCache();
        cache.Grant("Customer", "orders:read");
        var sut = new LayeredPermissionRegistry(Compiled(), cache);

        sut.HasPermission(CustomerRole, "orders:read").Should().BeTrue();
        cache.Clear();

        sut.HasPermission(CustomerRole, "orders:read").Should()
            .BeFalse("the registry holds no snapshot of its own, so a revoke takes effect on the next check");
    }

    [Fact]
    public void HasPermission_WhileTheCacheIsStillCold_FallsBackToTheCompiledLayerOnly()
    {
        // Fail closed: a stored grant is invisible until the first snapshot load completes.
        var cache = new FakeGrantCache();
        var sut = new LayeredPermissionRegistry(Compiled(("Admin", "orders:write")), cache);

        sut.HasPermission(AdminRole, "orders:write").Should().BeTrue();
        sut.HasPermission(AdminRole, "orders:delete").Should().BeFalse();
    }

    private static PermissionRegistry Compiled(params (string Role, string Permission)[] grants)
    {
        var builder = new PermissionRegistryBuilder();
        foreach (var (role, permission) in grants)
        {
            builder.Grant(role, permission);
        }

        return builder.Build();
    }

    /// <summary>In-memory <see cref="IPermissionGrantCache"/> recording which roles were consulted.</summary>
    private sealed class FakeGrantCache : IPermissionGrantCache
    {
        private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reads { get; } = [];

        public void Grant(string role, string permission)
        {
            if (!_grants.TryGetValue(role, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _grants[role] = set;
            }

            set.Add(permission);
        }

        public void Clear() => _grants.Clear();

        public IReadOnlySet<string> GetPermissions(string role)
        {
            Reads.Add(role);
            return _grants.TryGetValue(role, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
