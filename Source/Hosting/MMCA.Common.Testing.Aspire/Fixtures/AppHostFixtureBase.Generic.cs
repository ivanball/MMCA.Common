using Aspire.Hosting.Testing;

namespace MMCA.Common.Testing.Aspire.Fixtures;

/// <summary>
/// The usual <see cref="AppHostFixtureBase"/>: it boots the AppHost whose entry point is
/// <typeparamref name="TAppHost"/>.
/// <para>
/// <typeparamref name="TAppHost"/> is a type in the AppHost assembly, which is how the testing
/// builder finds the entry point to run. The Aspire AppHost SDK generates a public
/// <c>Projects.&lt;AppHost&gt;</c> marker into that assembly, so a test project takes a plain
/// <c>ProjectReference</c> to the AppHost (never an Aspire project resource) and names that marker.
/// </para>
/// <para>
/// A subclass is usually empty apart from its overrides: the resources to await, the budgets, and
/// <c>ConfigureBuilder</c> for test-only configuration.
/// </para>
/// </summary>
/// <typeparam name="TAppHost">A type in the AppHost assembly, normally its generated project marker.</typeparam>
public abstract class AppHostFixtureBase<TAppHost> : AppHostFixtureBase
    where TAppHost : class
{
    /// <summary>
    /// Command-line arguments handed to the AppHost. Empty by default; an AppHost that branches on
    /// its own arguments (a scenario switch, a publisher selection) overrides this.
    /// </summary>
    protected virtual IReadOnlyList<string> AppHostArguments => [];

    /// <inheritdoc />
    protected override async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken) =>
        await DistributedApplicationTestingBuilder
            .CreateAsync<TAppHost>([.. AppHostArguments], ConfigureOptions, cancellationToken)
            .ConfigureAwait(false);
}
