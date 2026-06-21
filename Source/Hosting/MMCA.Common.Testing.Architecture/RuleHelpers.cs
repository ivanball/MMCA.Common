namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Reflection helpers shared by the reflection-based fitness functions. NetArchTest cannot inspect
/// method return types, generic-argument constraints, property accessors, or attribute usage, so
/// those rules reflect over loaded types directly via these helpers.
/// </summary>
internal static class RuleHelpers
{
    /// <summary>Types that actually loaded, tolerating a partially-resolvable assembly.</summary>
    internal static IEnumerable<Type> GetLoadableTypes(this Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // OfType<Type>() both filters nulls and narrows the element type — no null-forgiving needed.
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>Concrete (non-abstract, non-interface) classes in an assembly.</summary>
    internal static IEnumerable<Type> ConcreteClasses(this Assembly assembly) =>
        assembly.GetLoadableTypes().Where(t => t is { IsClass: true, IsAbstract: false });

    /// <summary>
    /// The type's simple name with the generic-arity marker stripped, so suffix conventions match on
    /// generic types too (e.g. <c>EFRepository`2</c> → <c>EFRepository</c>, <c>DeleteEntityCommand`2</c>
    /// → <c>DeleteEntityCommand</c>).
    /// </summary>
    internal static string SimpleName(this Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }

    /// <summary>True if <paramref name="type"/> derives from the given open generic base type.</summary>
    internal static bool InheritsGeneric(this Type type, Type openGenericBase)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == openGenericBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if <paramref name="type"/> implements the given open generic interface.</summary>
    internal static bool ImplementsGeneric(this Type type, Type openGenericInterface) =>
        type.GetInterfaces().Any(i => i.IsGenericType
            && i.GetGenericTypeDefinition() == openGenericInterface);

    /// <summary>
    /// True if any base type's full name starts with the given prefix — used to detect framework
    /// base types (e.g. <c>FluentValidation.AbstractValidator`1</c>) without a compile dependency.
    /// </summary>
    internal static bool HasBaseTypeStartingWith(this Type type, string fullNamePrefix)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.FullName?.StartsWith(fullNamePrefix, StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Public instance properties declared on the type (not inherited).</summary>
    internal static IEnumerable<PropertyInfo> DeclaredPublicProperties(this Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    /// <summary>
    /// True if a property has a publicly-settable, non-init setter — i.e. it is mutable after
    /// construction. <c>init</c>-only setters carry the <c>IsExternalInit</c> modreq and are treated
    /// as immutable.
    /// </summary>
    internal static bool HasPublicMutableSetter(this PropertyInfo property)
    {
        var setter = property.SetMethod;
        if (setter is null || !setter.IsPublic)
        {
            return false;
        }

        var isInitOnly = setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        return !isInitOnly;
    }

    /// <summary>The aggregate-root base type name used across MMCA modules.</summary>
    internal static bool InheritsAggregateRoot(this Type type) =>
        type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableAggregateRootEntity");

    /// <summary>The auditable-entity base type name (covers child entities and aggregate roots).</summary>
    internal static bool InheritsAuditableEntity(this Type type) =>
        type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableBaseEntity")
        || type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableAggregateRootEntity")
        || type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.BaseEntity");
}
