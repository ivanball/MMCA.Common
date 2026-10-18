using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Testing.Conformance;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Exercises <see cref="TenantResolutionTestsBase"/> against the framework's own defaults: tenancy
/// switched on with nothing else configured is the compliant shape every consumer starts from, so
/// the inherited facts run here exactly as they run in a consumer's sealed subclass.
/// </summary>
public sealed class TenantResolutionConformanceTests : TenantResolutionTestsBase
{
    protected override TenancySettings Settings { get; } = new() { Enabled = true };
}
