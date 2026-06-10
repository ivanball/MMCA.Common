using MMCA.Common.Architecture.Tests.Helpers;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// DDD fitness functions for aggregate roots. NetArchTest can't inspect method return types, so
/// these reflect over the Domain assembly directly. Cross-aggregate navigation properties are
/// deliberately NOT forbidden — the framework uses them for the navigation-populator eager-loading
/// pattern (ADR-002).
/// </summary>
public sealed class AggregateConventionTests
{
    private static readonly Type[] AggregateRoots = PackageAssemblies.Domain.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && InheritsAggregateRoot(t))
        .ToArray();

    [Fact]
    public void Domain_ShouldExpose_AggregateRoots()
        => AggregateRoots.Should().NotBeEmpty(
            because: "the reflection filter must actually find aggregate roots, or this suite is vacuous");

    [Fact]
    public void AggregateRoots_ShouldHave_StaticCreateFactory_ReturningResultOfTheAggregate()
    {
        var violations = new List<string>();

        foreach (var type in AggregateRoots)
        {
            var createMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.Ordinal))
                .ToList();

            if (createMethods.Count == 0)
            {
                violations.Add($"  - {type.FullName}: no public static Create(...) factory");
            }
            else if (!createMethods.Exists(m => IsResultOf(m.ReturnType, type)))
            {
                violations.Add($"  - {type.FullName}: Create(...) must return Result<{type.Name}>");
            }
        }

        violations.Should().BeEmpty(
            because: "every aggregate root must be constructed via a static Create factory returning "
                + $"Result<T> (DDD factory convention).{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool InheritsAggregateRoot(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType
                && baseType.GetGenericTypeDefinition() == typeof(AuditableAggregateRootEntity<>))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsResultOf(Type returnType, Type aggregateType)
        => returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Result<>)
            && returnType.GetGenericArguments()[0] == aggregateType;
}
