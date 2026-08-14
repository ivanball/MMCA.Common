using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

/// <summary>
/// Coverage for the scoped tenant context invariants: unresolved until told, idempotent for the
/// value it already holds, and immovable once set.
/// </summary>
public sealed class TenantContextTests
{
    [Fact]
    public void NewContext_IsUnresolved()
    {
        var sut = new TenantContext();

        sut.TenantId.Should().BeNull();
        sut.IsResolved.Should().BeFalse("an unresolved tenant is a real state, not a missing value");
    }

    [Fact]
    public void SetTenant_ResolvesTheScope()
    {
        var sut = new TenantContext();

        sut.SetTenant("acme");

        sut.TenantId.Should().Be("acme");
        sut.IsResolved.Should().BeTrue();
    }

    [Fact]
    public void SetTenant_WithTheSameValue_IsIdempotent()
    {
        var sut = new TenantContext();
        sut.SetTenant("acme");

        var act = () => sut.SetTenant("acme");

        act.Should().NotThrow("re-asserting the same tenant on a scope must not be a fight");
        sut.TenantId.Should().Be("acme");
    }

    [Fact]
    public void SetTenant_WithADifferentValue_Throws()
    {
        var sut = new TenantContext();
        sut.SetTenant("acme");

        var act = () => sut.SetTenant("globex");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*acme*globex*",
                "anything already read in this scope was scoped to the first tenant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetTenant_WithABlankValue_Throws(string? tenantId)
    {
        var sut = new TenantContext();

        var act = () => sut.SetTenant(tenantId!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetTenant_IsCaseSensitive()
    {
        var sut = new TenantContext();
        sut.SetTenant("acme");

        var act = () => sut.SetTenant("ACME");

        act.Should().Throw<InvalidOperationException>(
            "tenant identifiers are compared ordinally everywhere in the framework, so two casings are two tenants");
    }
}
