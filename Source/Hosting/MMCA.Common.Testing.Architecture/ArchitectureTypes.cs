using MMCA.Common.Testing.Architecture.Metadata;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// The types of one compiled assembly, narrowed by predicates and then held to a dependency condition:
/// <code>
/// ArchitectureTypes.InAssembly(assembly)
///     .That().HaveNameEndingWith("Controller")
///     .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore");
/// </code>
/// The assembly is read from its file through <c>System.Reflection.Metadata</c>, never loaded, so a
/// type whose dependencies cannot be resolved at run time is still inspected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which types.</b> Every type the assembly defines, nested types included, except the ones nobody
/// wrote by hand: types marked <c>[CompilerGenerated]</c> or <c>[GeneratedCode]</c> (a nested one's content
/// counts towards the type it is nested in instead), the <c>&lt;Module&gt;</c> and
/// <c>&lt;PrivateImplementationDetails&gt;</c> pseudo-types, top-level types whose name starts with
/// <c>&lt;&gt;</c> (anonymous types), and types declared in the namespaces of the runtime and its
/// tooling (<c>System</c>, <c>Microsoft</c>, <c>Internal.Microsoft</c>, <c>netstandard</c>,
/// <c>NuGet</c>, <c>Newtonsoft</c>, <c>xunit</c>), which is where an assembly puts the extension-method
/// classes it adds to a framework namespace. An assembly whose own name falls under one of those
/// namespaces yields no types at all.
/// </para>
/// <para>
/// <b>Dependency names</b> are namespaces or type full names (<c>Azure</c>,
/// <c>System.Security.Cryptography.Rfc2898DeriveBytes</c>), matched as whole dot-separated segments:
/// <c>Azure</c> matches every type in <c>Azure</c> and in any namespace under it, <c>MMCA.Common.AI</c>
/// does not match <c>MMCA.Common.API</c>, and an entry ending in a dot matches nothing. What counts
/// as a dependency: base type, interfaces, attributes, generic constraints, member signatures, local
/// variables, and every type a method body names through its instructions, the bodies of lambdas,
/// local functions and async or iterator methods included.
/// </para>
/// </remarks>
public sealed class ArchitectureTypes
{
    private static readonly DependencyNameTree ExcludedNamespaces = new(
        ["System", "Microsoft", "netstandard", "NuGet", "Newtonsoft", "xunit", "Internal.Microsoft"]);

    private readonly IReadOnlyList<MetadataType> _types;

    private ArchitectureTypes(IReadOnlyList<MetadataType> types) => _types = types;

    /// <summary>Selects every type of <paramref name="assembly"/> (see the remarks for the exclusions).</summary>
    /// <param name="assembly">An assembly loaded from a file.</param>
    /// <returns>The selection.</returns>
    /// <exception cref="ArgumentException">The assembly was not loaded from a file.</exception>
    public static ArchitectureTypes InAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrEmpty(assembly.Location))
        {
            throw new ArgumentException(
                $"{assembly.GetName().Name} has no file location, so its metadata cannot be read.",
                nameof(assembly));
        }

        MetadataModule module;
        try
        {
            module = MetadataModule.Load(assembly.Location);
        }
        catch (BadImageFormatException)
        {
            return new ArchitectureTypes([]);
        }

        return ExcludedNamespaces.MatchesAny(module.AssemblyName)
            ? new ArchitectureTypes([])
            : new ArchitectureTypes([.. module.Types.Where(IsHandWritten)]);
    }

    /// <summary>Introduces the predicates; reads as prose and changes nothing.</summary>
    /// <returns>This selection.</returns>
    public ArchitectureTypes That() => this;

    /// <summary>Narrows to the types with the given simple name, ignoring case and any generic arity marker.</summary>
    /// <param name="name">The simple type name (<c>PasswordHasher</c>).</param>
    /// <returns>The narrowed selection.</returns>
    public ArchitectureTypes HaveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var plain = WithoutArity(name);
        return Where(t => string.Equals(NameWithoutArity(t), plain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Narrows to the types whose simple name, without any generic arity marker, ends with <paramref name="suffix"/>, ignoring case.</summary>
    /// <param name="suffix">The name suffix (<c>Controller</c>).</param>
    /// <returns>The narrowed selection.</returns>
    public ArchitectureTypes HaveNameEndingWith(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return Where(t => NameWithoutArity(t).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Narrows to the types that declare the attribute with the given full name (inherited attributes do not count).</summary>
    /// <param name="attributeFullName">The attribute type's full name (<c>Some.Ns.ServiceContractAttribute</c>).</param>
    /// <returns>The narrowed selection.</returns>
    public ArchitectureTypes HaveAttribute(string attributeFullName)
    {
        ArgumentNullException.ThrowIfNull(attributeFullName);
        return Where(t => t.HasAttribute(attributeFullName));
    }

    /// <summary>Narrows to the types that depend on none of <paramref name="dependencies"/>.</summary>
    /// <param name="dependencies">Namespaces or type full names.</param>
    /// <returns>The narrowed selection.</returns>
    public ArchitectureTypes DoNotHaveDependencyOnAny(params string[] dependencies)
    {
        var names = new DependencyNameTree(dependencies);
        return Where(t => !TypeDependencyScan.DependsOnAny(t, names));
    }

    /// <summary>Starts a condition every selected type must meet.</summary>
    /// <returns>The condition builder.</returns>
    public ArchitectureTypeConditions Should() => new(_types, expected: true);

    /// <summary>Starts a condition no selected type may meet.</summary>
    /// <returns>The condition builder.</returns>
    public ArchitectureTypeConditions ShouldNot() => new(_types, expected: false);

    /// <summary>Gets the full names (reflection spelling, <c>Outer+Inner</c>) of the selected types.</summary>
    /// <returns>The type names, in metadata order.</returns>
    public IReadOnlyList<string> GetTypeNames() => [.. _types.Select(t => t.ReflectionFullName)];

    private static bool IsHandWritten(MetadataType type) =>
        !string.Equals(type.FullName, "<Module>", StringComparison.Ordinal)
        && !type.FullName.StartsWith("<>", StringComparison.Ordinal)
        && !type.FullName.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)
        && !ExcludedNamespaces.MatchesAny(type.FullName)
        && !type.IsCompilerGenerated;

    private static string NameWithoutArity(MetadataType type) =>
        type.HasGenericParameters ? WithoutArity(type.Name) : type.Name;

    private static string WithoutArity(string name)
    {
        var tick = name.AsSpan().LastIndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }

    private ArchitectureTypes Where(Func<MetadataType, bool> predicate) => new([.. _types.Where(predicate)]);
}

/// <summary>The dependency conditions a selection of <see cref="ArchitectureTypes"/> is held to.</summary>
public sealed class ArchitectureTypeConditions
{
    private readonly IReadOnlyList<MetadataType> _types;
    private readonly bool _expected;

    internal ArchitectureTypeConditions(IReadOnlyList<MetadataType> types, bool expected)
    {
        _types = types;
        _expected = expected;
    }

    /// <summary>Checks the selected types for a dependency on at least one of <paramref name="dependencies"/>.</summary>
    /// <param name="dependencies">Namespaces or type full names.</param>
    /// <returns>The outcome, naming every type on the wrong side of the condition.</returns>
    public ArchitectureRuleResult HaveDependencyOnAny(params string[] dependencies)
    {
        var names = new DependencyNameTree(dependencies);
        return Evaluate(t => TypeDependencyScan.DependsOnAny(t, names));
    }

    /// <summary>Checks the selected types for a dependency on every one of <paramref name="dependencies"/>.</summary>
    /// <param name="dependencies">Namespaces or type full names.</param>
    /// <returns>The outcome, naming every type on the wrong side of the condition.</returns>
    public ArchitectureRuleResult HaveDependencyOnAll(params string[] dependencies)
    {
        var names = new DependencyNameTree(dependencies);
        return Evaluate(t => TypeDependencyScan.DependsOnAll(t, names));
    }

    private ArchitectureRuleResult Evaluate(Func<MetadataType, bool> condition) =>
        new([.. _types.Where(t => condition(t) != _expected).Select(t => t.ReflectionFullName)]);
}
