using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// Assertions for the DI registration extension methods every module and layer exposes. The framework's
/// registration methods are fluent: each returns the <b>same</b> <see cref="IServiceCollection"/> instance
/// it was handed, so hosts can chain <c>AddApplication().AddInfrastructure(...).AddAPI(...)</c>. An
/// extension that returns a new collection (or a differently-built one) silently drops every registration
/// chained after it, which no other test catches because the dropped services are simply absent.
/// </summary>
public static class DependencyInjectionAssert
{
    /// <summary>
    /// Asserts that <paramref name="register"/> hands back the very collection it was given, so the fluent
    /// chain stays intact. Creates the collection itself, so a call site is one line:
    /// <c>DependencyInjectionAssert.ReturnsSameCollection(s =&gt; s.AddCatalogModule(settings));</c>
    /// </summary>
    /// <param name="register">The registration extension under test.</param>
    public static void ReturnsSameCollection(Func<IServiceCollection, IServiceCollection> register)
    {
        ArgumentNullException.ThrowIfNull(register);

        var services = new ServiceCollection();

        var result = register(services);

        result.Should().BeSameAs(
            services,
            because: "a fluent registration extension must return the collection it was handed, or every call chained after it registers into a collection the host never builds");
    }
}
