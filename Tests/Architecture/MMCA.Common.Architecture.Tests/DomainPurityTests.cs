using MMCA.Common.Architecture.Tests.Helpers;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Ensures Domain and Application layers remain framework-independent.
/// Domain code should contain only entities, value objects, aggregates,
/// domain services, domain events, and specifications — no framework dependencies.
/// Application code should not reference persistence or presentation frameworks.
/// </summary>
public sealed class DomainPurityTests
{
    private static readonly string[] ForbiddenDomainDependencies =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Serilog",
        "AutoMapper",
        "Newtonsoft.Json",
        "FluentValidation",
        "Scrutor",
        "MudBlazor",
        "Polly",
        "Stripe",
        "StackExchange.Redis",
    ];

    [Fact]
    public void Domain_ShouldNotDependOn_Frameworks()
    {
        var result = Types.InAssembly(PackageAssemblies.Domain)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenDomainDependencies)
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result,
            "Domain must be framework-independent — only pure C# and MMCA.Common.Shared allowed");
    }

    [Fact]
    public void Application_ShouldNotDependOn_EntityFrameworkCore()
    {
        var result = Types.InAssembly(PackageAssemblies.Application)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result,
            "Application must not depend on EF Core directly — use IRepository/IUnitOfWork abstractions");
    }

    [Fact]
    public void Application_ShouldNotDependOn_AspNetCore()
    {
        var result = Types.InAssembly(PackageAssemblies.Application)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result,
            "Application must not depend on ASP.NET Core — it should remain host-agnostic");
    }

    [Fact]
    public void Shared_ShouldNotDependOn_Frameworks()
    {
        var result = Types.InAssembly(PackageAssemblies.Shared)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenDomainDependencies)
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result,
            "Shared must be framework-independent — only foundational abstractions");
    }
}
