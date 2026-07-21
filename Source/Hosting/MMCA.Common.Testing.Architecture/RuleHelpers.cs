using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Reflection helpers shared by the reflection-based fitness functions. NetArchTest cannot inspect
/// method return types, generic-argument constraints, property accessors, or attribute usage, so
/// those rules reflect over loaded types directly via these helpers.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
internal static class RuleHelpers
{
    extension(Assembly assembly)
    {
        /// <summary>Gets the types that actually loaded, tolerating a partially-resolvable assembly.</summary>
        internal IEnumerable<Type> LoadableTypes
        {
            get
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // OfType<Type>() both filters nulls and narrows the element type: no null-forgiving needed.
                    return ex.Types.OfType<Type>();
                }
            }
        }

        /// <summary>Gets the concrete (non-abstract, non-interface) classes in the assembly.</summary>
        internal IEnumerable<Type> ConcreteClasses =>
            assembly.LoadableTypes.Where(t => t is { IsClass: true, IsAbstract: false });
    }

    extension(Type type)
    {
        /// <summary>
        /// Gets the type's simple name with the generic-arity marker stripped, so suffix conventions
        /// match on generic types too (e.g. <c>EFRepository`2</c> → <c>EFRepository</c>,
        /// <c>DeleteEntityCommand`2</c> → <c>DeleteEntityCommand</c>).
        /// </summary>
        internal string SimpleName
        {
            get
            {
                var name = type.Name;
                var tick = name.IndexOf('`', StringComparison.Ordinal);
                return tick >= 0 ? name[..tick] : name;
            }
        }

        /// <summary>True if the type derives from the given open generic base type.</summary>
        internal bool InheritsGeneric(Type openGenericBase)
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

        /// <summary>True if the type implements the given open generic interface.</summary>
        internal bool ImplementsGeneric(Type openGenericInterface) =>
            type.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == openGenericInterface);

        /// <summary>
        /// True if any base type's full name starts with the given prefix: used to detect framework
        /// base types (e.g. <c>FluentValidation.AbstractValidator`1</c>) without a compile dependency.
        /// </summary>
        internal bool HasBaseTypeStartingWith(string fullNamePrefix)
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

        /// <summary>Gets the public instance properties declared on the type (not inherited).</summary>
        internal IEnumerable<PropertyInfo> DeclaredPublicProperties =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        /// <summary>
        /// Gets a value indicating whether the type derives from the aggregate-root base type used
        /// across MMCA modules.
        /// </summary>
        internal bool InheritsAggregateRoot =>
            type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableAggregateRootEntity");

        /// <summary>
        /// Gets a value indicating whether the type derives from an auditable-entity base type
        /// (covers child entities and aggregate roots).
        /// </summary>
        internal bool InheritsAuditableEntity =>
            type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableBaseEntity")
            || type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.AuditableAggregateRootEntity")
            || type.HasBaseTypeStartingWith("MMCA.Common.Domain.Entities.BaseEntity");
    }

    extension(PropertyInfo property)
    {
        /// <summary>
        /// Gets a value indicating whether the property has a publicly-settable, non-init setter,
        /// i.e. it is mutable after construction. <c>init</c>-only setters carry the
        /// <c>IsExternalInit</c> modreq and are treated as immutable.
        /// </summary>
        internal bool HasPublicMutableSetter
        {
            get
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
        }
    }
}
