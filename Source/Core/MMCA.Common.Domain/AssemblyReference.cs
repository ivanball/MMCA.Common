using System.Reflection;

namespace MMCA.Common.Domain;

/// <summary>
/// Assembly anchor for the Domain layer: the same <c>AssemblyReference</c> / <c>ClassReference</c>
/// pair the Application and Infrastructure projects declare. No framework or consumer code
/// currently resolves the Domain assembly through it.
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
public class ClassReference;
