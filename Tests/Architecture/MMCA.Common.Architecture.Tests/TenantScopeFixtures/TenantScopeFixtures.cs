using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Architecture.Tests.TenantScopeFixtures;

/// <summary>Tenant-scoped: carries the marker. Neither tenancy rule may flag it.</summary>
public sealed class ScopedTenantFixture : AuditableBaseEntity<int>, ITenantEntity
{
    public string TenantId { get; private set; } = string.Empty;
}

/// <summary>No marker and no tenant column: a forgotten marker. The module rule must flag it.</summary>
public sealed class UnscopedTenantFixture : AuditableBaseEntity<int>
{
    public string Name { get; private set; } = string.Empty;
}

/// <summary>A tenant column without the marker: looks scoped, is not. The TenantId rule must flag it.</summary>
public sealed class TenantColumnWithoutMarkerFixture : AuditableBaseEntity<int>
{
    public string TenantId { get; private set; } = string.Empty;
}
