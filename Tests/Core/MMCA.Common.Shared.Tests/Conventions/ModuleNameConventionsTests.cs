using AwesomeAssertions;
using MMCA.Common.Shared.Conventions;
using MMCA.Common.Shared.Tests.Fakes.Sales.Application;
using MMCA.Common.Shared.Tests.Fakes.Sales.Domain;
using MMCA.Common.Shared.Tests.Fakes.Sales.Domain.Orders;

namespace MMCA.Common.Shared.Tests.Conventions;

/// <summary>
/// The module-name derivation is shared by two callers that must never disagree: persistence
/// (SQL schema and logical data-source names) and the CQRS logging decorators' scope enrichment.
/// These tests pin the parse against the workspace <c>MMCA.{App}.{Module}.{Layer}</c> convention,
/// including the namespaces that carry no module at all.
/// </summary>
public sealed class ModuleNameConventionsTests
{
    // ── Conventionally-named namespaces ──
    [Fact]
    public void GetModuleName_TypeWithDomainAsLastSegment_ReturnsPrecedingSegment() =>
        ModuleNameConventions.GetModuleName(typeof(SalesFakeAggregate)).Should().Be("Sales");

    [Fact]
    public void GetModuleName_TypeWithDomainInTheMiddle_ReturnsPrecedingSegment() =>
        ModuleNameConventions.GetModuleName(typeof(SalesFakeOrder)).Should().Be("Sales");

    [Fact]
    public void GetModuleName_TypeWithApplicationLayerSegment_ReturnsPrecedingSegment() =>
        ModuleNameConventions.GetModuleName(typeof(SalesFakeUseCase)).Should().Be(
            "Sales",
            "handlers live in module Application namespaces and must resolve their module");

    // ── Namespaces that carry no module ──
    [Fact]
    public void GetModuleName_TypeWithoutDomainSegment_ReturnsNull() =>
        ModuleNameConventions.GetModuleName(typeof(string)).Should().BeNull();

    [Fact]
    public void GetModuleName_TypeOutsideAModuleNamespace_ReturnsNull() =>
        ModuleNameConventions.GetModuleName(typeof(ModuleNameConventionsTests)).Should().BeNull();

    // Framework namespaces such as MMCA.Common.Application.* carry their layer segment at index 2,
    // below the minimum the non-Domain parse requires, so they resolve to no module. That behavior
    // is pinned where such a namespace naturally exists: the LoggingCommandDecorator tests in
    // MMCA.Common.Application.Tests assert the scope logs "unknown" for their own fake command.
    [Fact]
    public void GetModuleName_ClosedGenericType_ReadsItsOwnNamespaceNotItsArguments() =>
        ModuleNameConventions.GetModuleName(typeof(List<SalesFakeAggregate>)).Should().BeNull(
            "the parse keys on the type's own namespace (System.Collections.Generic here), never on its type arguments");
}
