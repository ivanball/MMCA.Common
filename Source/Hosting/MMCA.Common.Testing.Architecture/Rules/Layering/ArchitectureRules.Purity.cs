namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Frameworks that must never leak into a Domain (or Shared) layer. Repos may extend this via the
    /// <c>extra</c> parameter (e.g. Store adds "Stripe", ADC adds "RabbitMQ").
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenDomainDependencies =
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

    public static void DomainIsFrameworkFree(IArchitectureMap map, IEnumerable<string>? extra = null)
    {
        var forbidden = ForbiddenDomainDependencies.Concat(extra ?? []).ToArray();
        foreach (var domain in map.OfLayer(Layer.Domain))
        {
            var result = Types.InAssembly(domain)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{domain.GetName().Name}: Domain must be framework-independent — only pure C# and Shared contracts");
        }
    }

    public static void SharedIsFrameworkFree(IArchitectureMap map, IEnumerable<string>? extra = null)
    {
        var forbidden = ForbiddenDomainDependencies.Concat(extra ?? []).ToArray();
        foreach (var shared in map.OfLayer(Layer.Shared))
        {
            var result = Types.InAssembly(shared)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{shared.GetName().Name}: Shared must be framework-independent — only foundational abstractions");
        }
    }

    public static void ApplicationDoesNotDependOnEntityFrameworkCore(IArchitectureMap map)
    {
        foreach (var application in map.OfLayer(Layer.Application))
        {
            var result = Types.InAssembly(application)
                .ShouldNot()
                .HaveDependencyOnAny("Microsoft.EntityFrameworkCore")
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{application.GetName().Name}: Application must not depend on EF Core — use IRepository/IUnitOfWork");
        }
    }

    public static void ApplicationDoesNotDependOnAspNetCore(IArchitectureMap map)
    {
        foreach (var application in map.OfLayer(Layer.Application))
        {
            var result = Types.InAssembly(application)
                .ShouldNot()
                .HaveDependencyOnAny("Microsoft.AspNetCore")
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{application.GetName().Name}: Application must remain host-agnostic — no ASP.NET Core");
        }
    }
}
