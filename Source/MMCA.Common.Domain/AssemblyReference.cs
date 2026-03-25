using System.Reflection;

namespace MMCA.Common.Domain;

/// <summary>
/// Provides assembly metadata for Scrutor assembly-scanning registration and architecture tests.
/// </summary>
public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
    public static readonly string AssemblyName = Assembly.GetName().Name ?? string.Empty;
}

/// <summary>
/// Anchor type used for assembly resolution when <see cref="AssemblyReference"/> cannot be
/// used (e.g., generic type constraints that require a non-static class).
/// </summary>
public class ClassReference { }
