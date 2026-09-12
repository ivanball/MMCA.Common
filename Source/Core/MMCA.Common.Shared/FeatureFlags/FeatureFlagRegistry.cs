using System.Reflection;

namespace MMCA.Common.Shared.FeatureFlags;

/// <summary>
/// One feature flag constant as the registry reports it.
/// </summary>
/// <param name="FieldName">The C# field name, e.g. <c>PushNotifications</c>.</param>
/// <param name="FlagName">The flag's configuration value, e.g. <c>Notification.PushNotifications</c>.</param>
/// <param name="Lifetime">
/// The declared lifetime, or <see langword="null"/> when the field carries no
/// <see cref="FeatureFlagAttribute"/> (which the fitness rules fail the build on, but which the
/// registry reports rather than hides).
/// </param>
/// <param name="RemoveBy">The declared removal date as ISO <c>yyyy-MM-dd</c>, or <see langword="null"/>.</param>
/// <param name="Owner">The declared owner, or <see langword="null"/>.</param>
public sealed record FeatureFlagDescriptor(
    string FieldName,
    string FlagName,
    FeatureFlagLifetime? Lifetime,
    string? RemoveBy,
    string? Owner);

/// <summary>
/// Reads the feature flags an assembly declares: every <c>public const string</c> on a static class
/// whose name ends with <c>Features</c>, with whatever <see cref="FeatureFlagAttribute"/> it
/// carries.
/// <para>
/// Public on purpose. The fitness rules in <c>MMCA.Common.Testing.Architecture</c> deliberately do
/// NOT use this (that package takes no framework reference and matches by name instead), so this is
/// the runtime half: a host can surface its own flag inventory, with owners and removal dates, on an
/// administration endpoint without re-deriving the convention.
/// </para>
/// </summary>
public static class FeatureFlagRegistry
{
    /// <summary>The suffix a feature-flag constant class is named with.</summary>
    public const string FeatureClassSuffix = "Features";

    /// <summary>
    /// Describes every feature flag declared by the given assemblies, ordered by flag name.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan (typically each module's <c>*.Shared</c> assembly).</param>
    /// <returns>The declared flags; empty when none of the assemblies declares one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblies"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<FeatureFlagDescriptor> Describe(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return [.. assemblies
            .SelectMany(LoadableTypes)
            .Where(IsFeatureClass)
            .SelectMany(FlagFields)
            .Select(Describe)
            .OrderBy(d => d.FlagName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Describes every feature flag declared by one assembly, ordered by flag name.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The declared flags; empty when the assembly declares none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<FeatureFlagDescriptor> Describe(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Describe([assembly]);
    }

    /// <summary>
    /// Gets a value indicating whether a type is a feature-flag constant class: a static class whose
    /// name ends with <see cref="FeatureClassSuffix"/>.
    /// </summary>
    /// <param name="type">The candidate type.</param>
    /// <returns><see langword="true"/> when the type holds feature-flag constants by convention.</returns>
    public static bool IsFeatureClass(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type is { IsClass: true, IsAbstract: true, IsSealed: true }
            && type.Name.EndsWith(FeatureClassSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the <c>public const string</c> fields of a feature-flag constant class.
    /// </summary>
    /// <param name="type">The feature-flag constant class.</param>
    /// <returns>Its flag fields, in declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static IEnumerable<FieldInfo> FlagFields(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string));
    }

    private static FeatureFlagDescriptor Describe(FieldInfo field)
    {
        var attribute = field.GetCustomAttribute<FeatureFlagAttribute>(inherit: false);

        return new FeatureFlagDescriptor(
            field.Name,
            field.GetRawConstantValue() as string ?? string.Empty,
            attribute?.Lifetime,
            attribute?.RemoveBy,
            attribute?.Owner);
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // OfType<Type>() both filters the nulls and narrows the element type.
            return ex.Types.OfType<Type>();
        }
    }
}
