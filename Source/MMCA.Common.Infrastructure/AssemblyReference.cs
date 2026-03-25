using System.Reflection;

namespace MMCA.Common.Infrastructure;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
    public static readonly string AssemblyName = Assembly.GetName().Name ?? string.Empty;
}

public class ClassReference { }
