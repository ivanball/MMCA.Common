namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// Sets one process-wide environment variable for the lifetime of the scope and restores the previous
/// value on dispose. Only for tests in <see cref="E2EEnvironmentCollection"/>, which run alone.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvironmentScope(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
}
