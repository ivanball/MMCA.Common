using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.Support;
using Xunit;

namespace MMCA.Common.Testing.Tests.Support;

/// <summary>
/// Unit tests for <c>ReplaceWithFake</c>: the one-line swap a test factory makes inside
/// <c>ConfigureTestServices</c>. The load-bearing parts are that a fake for a service the host never
/// registered fails loudly (a stale fake, not a silent no-op), that EVERY unkeyed registration goes
/// (unlike <c>Replace</c>, which drops only the first), and that the fake inherits the lifetime DI
/// would actually have resolved, which is the LAST registration's.
/// </summary>
public sealed class ServiceCollectionFakeExtensionsTests
{
    private const string ServiceKey = "keyed";

    private interface IFoo
    {
        string Name { get; }
    }

    private interface IUnrelated
    {
        string Name { get; }
    }

    [Fact]
    public void NeverRegistered_Throws_NamingTheService()
    {
        // Arrange: one unrelated registration, so "unchanged" is a count of one rather than zero.
        var services = new ServiceCollection();
        services.AddSingleton<IUnrelated, Unrelated>();
        var fullName = typeof(IFoo).FullName!;

        // Act
        var parameterless = () => services.ReplaceWithFake<IFoo, FakeFoo>();
        var withLifetime = () => services.ReplaceWithFake<IFoo, FakeFoo>(ServiceLifetime.Singleton);
        var withInstance = () => services.ReplaceWithFake<IFoo>(new FakeFoo());

        // Assert
        parameterless.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(fullName).And.Contain("never registered");
        withLifetime.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(fullName).And.Contain("never registered");
        withInstance.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(fullName).And.Contain("never registered");
        services.Should().HaveCount(1, "a refused swap must leave the collection exactly as it found it");
    }

    [Fact]
    public void RemovesEveryRegistration_NotOnlyTheFirst()
    {
        // Arrange: two registrations of the service with an unrelated one between them.
        var services = new ServiceCollection();
        services.AddScoped<IFoo, RealFoo>();
        services.AddSingleton<IUnrelated, Unrelated>();
        services.AddScoped<IFoo, OtherRealFoo>();

        // Act
        services.ReplaceWithFake<IFoo, FakeFoo>();

        // Assert
        var remaining = UnkeyedFoo(services);
        remaining.Should().ContainSingle("Replace drops only the first registration; this helper drops every one");
        remaining[0].ImplementationType.Should().Be<FakeFoo>();
        services[^1].Should().BeSameAs(remaining[0], "the fake is appended at the end of the collection");
        services.Should().Contain(d => d.ServiceType == typeof(IUnrelated), "other services are untouched");
    }

    [Fact]
    public void CopiesTheLifetime_OfTheLastRegistration()
    {
        // Arrange: the last registration is the one DI resolves, so its lifetime is the one to copy.
        var services = new ServiceCollection();
        services.AddSingleton<IFoo, RealFoo>();
        services.AddTransient<IFoo, OtherRealFoo>();

        // Act
        services.ReplaceWithFake<IFoo, FakeFoo>();

        // Assert
        var fake = UnkeyedFoo(services).Should().ContainSingle().Subject;
        fake.Lifetime.Should().Be(ServiceLifetime.Transient);
        fake.ImplementationType.Should().Be<FakeFoo>();
    }

    [Fact]
    public void ExplicitLifetime_Wins()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<IFoo, RealFoo>();

        // Act
        services.ReplaceWithFake<IFoo, FakeFoo>(ServiceLifetime.Singleton);

        // Assert
        var fake = UnkeyedFoo(services).Should().ContainSingle().Subject;
        fake.Lifetime.Should().Be(ServiceLifetime.Singleton);
        fake.ImplementationType.Should().Be<FakeFoo>();
    }

    [Fact]
    public void Instance_RegistersASingletonInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<IFoo, RealFoo>();
        services.AddTransient<IFoo, OtherRealFoo>();
        var instance = new FakeFoo();

        // Act
        services.ReplaceWithFake<IFoo>(instance);

        // Assert
        var fake = UnkeyedFoo(services).Should().ContainSingle().Subject;
        fake.Lifetime.Should().Be(ServiceLifetime.Singleton);
        fake.ImplementationInstance.Should().BeSameAs(instance);
        services[^1].Should().BeSameAs(fake);
    }

    [Fact]
    public void Instance_Null_Throws()
    {
        // Arrange: the service IS registered, so the only reason left to refuse is the null.
        var services = new ServiceCollection();
        services.AddScoped<IFoo, RealFoo>();

        // Act
        var act = () => services.ReplaceWithFake<IFoo>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void KeyedRegistrations_AreUntouched_AndDoNotCount()
    {
        // Arrange: a keyed registration alone is not "registered" for an unkeyed swap.
        var keyedOnly = new ServiceCollection();
        keyedOnly.AddKeyedScoped<IFoo, RealFoo>(ServiceKey);

        // Act
        var act = () => keyedOnly.ReplaceWithFake<IFoo, FakeFoo>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(typeof(IFoo).FullName!).And.Contain("never registered");
        keyedOnly.Should().ContainSingle().Which.IsKeyedService.Should().BeTrue("a refused swap removes nothing");

        // Arrange: keyed plus unkeyed, the swap touches only the unkeyed one.
        var both = new ServiceCollection();
        both.AddKeyedScoped<IFoo, RealFoo>(ServiceKey);
        both.AddScoped<IFoo, OtherRealFoo>();

        // Act
        both.ReplaceWithFake<IFoo, FakeFoo>();

        // Assert
        var keyed = both.Where(static d => d.ServiceType == typeof(IFoo) && d.IsKeyedService).ToList();
        keyed.Should().ContainSingle("a keyed registration is neither removed nor counted");
        keyed[0].ServiceKey.Should().Be(ServiceKey);
        keyed[0].KeyedImplementationType.Should().Be<RealFoo>();
        UnkeyedFoo(both).Should().ContainSingle().Which.ImplementationType.Should().Be<FakeFoo>();
    }

    [Fact]
    public void ReturnsTheSameCollection()
    {
        DependencyInjectionAssert.ReturnsSameCollection(static services =>
        {
            services.AddScoped<IFoo, RealFoo>();
            return services.ReplaceWithFake<IFoo, FakeFoo>();
        });

        DependencyInjectionAssert.ReturnsSameCollection(static services =>
        {
            services.AddScoped<IFoo, RealFoo>();
            return services.ReplaceWithFake<IFoo, FakeFoo>(ServiceLifetime.Singleton);
        });

        DependencyInjectionAssert.ReturnsSameCollection(static services =>
        {
            services.AddScoped<IFoo, RealFoo>();
            return services.ReplaceWithFake<IFoo>(new FakeFoo());
        });
    }

    // Keyed descriptors throw on ImplementationType, so filter them out BEFORE reading it.
    private static List<ServiceDescriptor> UnkeyedFoo(IServiceCollection services) =>
        [.. services.Where(static d => d.ServiceType == typeof(IFoo) && !d.IsKeyedService)];

    private sealed class RealFoo : IFoo
    {
        public string Name => nameof(RealFoo);
    }

    private sealed class OtherRealFoo : IFoo
    {
        public string Name => nameof(OtherRealFoo);
    }

    private sealed class FakeFoo : IFoo
    {
        public string Name => nameof(FakeFoo);
    }

    private sealed class Unrelated : IUnrelated
    {
        public string Name => nameof(Unrelated);
    }
}
