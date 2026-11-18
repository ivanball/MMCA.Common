using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MMCA.Common.Testing.Conformance.UiHost;

/// <summary>
/// Pins that a Blazor UI host wires the framework's own-host UI hardening (ADR-088): the ceiling on
/// concurrently ACTIVE circuits is registered as a singleton, and the UI edge rate limiter is
/// registered as the global limiter rejecting with 429. The handler's and the limiter's own behaviour
/// (the ceiling, the partitions, the exemptions) is the framework's and is covered in
/// MMCA.Common.UI.Web.Tests; what only a consumer can answer is whether its real host registers them.
/// </summary>
/// <remarks>
/// A consumer keeps a small sealed subclass (the ADR-015 pattern) that implements
/// <c>IClassFixture</c> over its own UI host factory and returns it from <see cref="Factory"/>.
/// Host-specific numbers (circuit retention, the rate-limit window) stay as facts in the subclass.
/// </remarks>
/// <typeparam name="TEntryPoint">The UI host's entry-point class, typically its <c>Program</c>.</typeparam>
/// <typeparam name="TCircuitCeiling">
/// The circuit handler that enforces the ceiling, the framework's <c>BoundedCircuitHandler</c>. It is
/// a type parameter rather than a named type because this package does not reference
/// MMCA.Common.UI.Web, and taking that reference would drag the UI host layer into every integration
/// test project that uses this package.
/// </typeparam>
public abstract class UiHostHardeningTestsBase<TEntryPoint, TCircuitCeiling>
    where TEntryPoint : class
    where TCircuitCeiling : CircuitHandler
{
    /// <summary>Gets the booted UI host under test, typically the xUnit class fixture's factory.</summary>
    protected abstract WebApplicationFactory<TEntryPoint> Factory { get; }

    /// <summary>
    /// The handler is only a control if the real host registers it, and it has to be a SINGLETON: a
    /// scoped registration is resolved once per circuit, so every circuit would count to one and the
    /// ceiling would never be reached.
    /// </summary>
    [Fact]
    public void Host_RegistersTheCeilingAsASingleton()
    {
        using var client = Factory.CreateClient();

        var handlers = Factory.Services.GetServices<CircuitHandler>().ToList();
        var first = Factory.Services.GetServices<CircuitHandler>().OfType<TCircuitCeiling>().Single();
        using var scope = Factory.Services.CreateScope();
        var fromScope = scope.ServiceProvider.GetServices<CircuitHandler>().OfType<TCircuitCeiling>().Single();

        handlers.Should().ContainItemsAssignableTo<CircuitHandler>();
        fromScope.Should().BeSameAs(first, "one count must span the replica, not one count per circuit");
    }

    /// <summary>
    /// The load-bearing registration assertion: a public UI origin that called neither
    /// <c>AddRateLimiter</c> nor anything equivalent lets an unauthenticated flood reach the Blazor
    /// pipeline unmetered while the Gateway's edge limiter watches a different FQDN. Resolving the
    /// options that only the limiter registers proves the wiring is present in the real host.
    /// </summary>
    [Fact]
    public void Host_RegistersTheEdgeRateLimiter()
    {
        using var client = Factory.CreateClient();

        var limiterOptions = Factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        // Both assertions fail on an unconfigured host: GlobalLimiter defaults to null and
        // RejectionStatusCode defaults to 503.
        limiterOptions.GlobalLimiter.Should().NotBeNull(
            "the chained per-IP window and concurrency ceiling are registered as the global limiter");
        limiterOptions.RejectionStatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
    }
}
