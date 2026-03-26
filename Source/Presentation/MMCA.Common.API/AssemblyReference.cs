using System.Reflection;

namespace MMCA.Common.API;

/// <summary>
/// Provides a stable reference to this assembly for use in assembly scanning (e.g., Scrutor registration).
/// </summary>
public static class AssemblyReference
{
    /// <summary>The <see cref="System.Reflection.Assembly"/> instance for MMCA.Common.API.</summary>
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;

    /// <summary>The assembly name string for logging and diagnostics.</summary>
    public static readonly string AssemblyName = Assembly.GetName().Name ?? string.Empty;
}

/// <summary>
/// Marker class used when a type reference is needed for assembly scanning rather than an assembly reference.
/// </summary>
public class ClassReference { }
