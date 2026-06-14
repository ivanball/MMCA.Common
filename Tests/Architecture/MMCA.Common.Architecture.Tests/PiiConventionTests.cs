using System.Reflection;
using MMCA.Common.Architecture.Tests.Helpers;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Framework-side guardrail for the PII right-to-erasure governance (rubric §30, ADR-005): any
/// <c>MMCA.Common.Domain</c> entity that declares a <see cref="PiiAttribute"/>-marked property must
/// implement <see cref="IAnonymizable"/>. Passes vacuously today (the framework ships no PII-bearing
/// data-subject entity) and fails the build the moment one is added without an erasure path.
/// </summary>
public sealed class PiiConventionTests
{
    [Fact]
    public void EntitiesWithPiiProperties_ShouldImplement_IAnonymizable()
    {
        var piiEntities = Types.InAssembly(PackageAssemblies.Domain)
            .GetTypes().Select(t => t.ReflectionType)
            .Where(HasPiiProperty);

        foreach (var entityType in piiEntities)
        {
            typeof(IAnonymizable).IsAssignableFrom(entityType).Should().BeTrue(
                because: $"{entityType.FullName} declares [Pii] properties and must implement IAnonymizable (ADR-005)");
        }
    }

    private static bool HasPiiProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.GetCustomAttribute<PiiAttribute>() is not null);
}
