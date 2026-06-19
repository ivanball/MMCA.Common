using System.Reflection;
using MMCA.Common.Architecture.Tests.Helpers;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Framework-side guardrail for integration-event schema versioning (rubric §6, ADR-010): every
/// concrete <see cref="IIntegrationEvent"/> must expose an <c>int SchemaVersion</c> (supplied by
/// <c>BaseIntegrationEvent</c>, default <c>1</c>) so cross-service consumers have an explicit version
/// signal to branch/upcast on rather than resolving by type string alone. Passes vacuously today (the
/// framework ships no concrete integration event) and fails the build the moment one is added that
/// carries no version — e.g. a type implementing <see cref="IIntegrationEvent"/> directly without
/// inheriting <c>BaseIntegrationEvent</c>.
/// </summary>
public sealed class EventVersioningConventionTests
{
    [Fact]
    public void IntegrationEvents_ShouldDeclare_SchemaVersion()
    {
        var integrationEvents = Types.InAssembly(PackageAssemblies.Domain)
            .GetTypes().Select(t => t.ReflectionType)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t));

        foreach (var eventType in integrationEvents)
        {
            var versionProperty = eventType.GetProperty("SchemaVersion", BindingFlags.Public | BindingFlags.Instance);

            // Asserting on the nullable PropertyType covers both "the property exists" and "it is an
            // int" in one check, avoiding a null-forgiving access (IDE0370) after NotBeNull().
            (versionProperty?.PropertyType).Should().Be(typeof(int),
                because: $"{eventType.FullName} is an integration event and must declare an int SchemaVersion (ADR-010)");
        }
    }
}
