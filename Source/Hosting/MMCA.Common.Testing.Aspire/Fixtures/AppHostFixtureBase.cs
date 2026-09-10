using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Testing.Aspire.Preconditions;
using Xunit;

namespace MMCA.Common.Testing.Aspire.Fixtures;

/// <summary>
/// Collection fixture that boots a real Aspire AppHost once, waits for its resources to report
/// ready, and hands the running application to every test in the collection.
/// <para>
/// The AppHost is the one file that states how a whole stack fits together (project resources,
/// per-service databases, brokers, JWKS and gRPC references, the WaitFor graph) and nothing else
/// compiles a claim about it: a build never runs it, and an in-process integration tier boots its
/// hosts directly, bypassing the orchestration entirely. A renamed resource, a reference that no
/// longer resolves or a WaitFor cycle is invisible to every other tier.
/// </para>
/// <para>
/// <b>Readiness, not liveness, and per resource.</b> "The application started" only means the
/// orchestrator launched the processes. Waiting on each resource's own health signal is what makes a
/// test that runs next actually able to talk to it. Resources that carry a health check are awaited
/// healthy; resources that carry none can only be awaited Running, and the fixture says which
/// happened rather than pretending they are the same.
/// </para>
/// <para>
/// <b>Preconditions are a skip, not a failure.</b> Booting an AppHost needs an opt-in, usually a
/// container runtime, and sometimes the HTTPS development certificate. When one is missing,
/// <see cref="SkipReason"/> is set and nothing is started, so a test class can skip the collection
/// with a sentence a human can act on. The consuming test project owns the skip call itself (this
/// package deliberately takes no dependency on the xUnit assertion library):
/// <c>Assert.SkipWhen(!Fixture.IsAvailable, Fixture.SkipReason!)</c>.
/// </para>
/// </summary>
public abstract class AppHostFixtureBase : IAsyncLifetime
{
    /// <summary>
    /// Original values of every environment variable this fixture pushed, so a collection cannot
    /// leak key material or configuration into the ones that run after it. Only the FIRST original
    /// value is recorded, so re-pushing a key cannot clobber the restore point.
    /// </summary>
    private readonly Dictionary<string, string?> _originalEnvironment = [];

    private DistributedApplication? _application;

    /// <summary>
    /// Why the collection was skipped, or <see langword="null"/> when it ran. Set by the
    /// precondition gate before anything is started.
    /// </summary>
    public string? SkipReason { get; private set; }

    /// <summary>Whether the preconditions were met and <see cref="Application"/> is usable.</summary>
    public bool IsAvailable => SkipReason is null;

    /// <summary>
    /// The keypair minted for this collection, or <see langword="null"/> when the environment already
    /// carried one (or when <see cref="SuppliesE2eRsaKeys"/> is off). A test that needs to sign a
    /// token the running stack will accept signs it with this private key.
    /// </summary>
    public EphemeralRsaKeyPair? E2eRsaKeys { get; private set; }

    /// <summary>The running application.</summary>
    /// <exception cref="InvalidOperationException">The collection was skipped or has not started.</exception>
    public DistributedApplication Application => _application ?? throw new InvalidOperationException(
        SkipReason ?? "The AppHost has not been started; use the fixture through a collection so InitializeAsync runs first.");

    /// <summary>
    /// What this AppHost needs from the machine. Defaults to opt-in plus a container runtime, which
    /// is the shape of every real stack; a project-only AppHost can drop
    /// <see cref="AppHostEnvironmentRequirement.Docker"/>, and a stack with an <c>https</c> launch
    /// profile should add <see cref="AppHostEnvironmentRequirement.DeveloperCertificate"/>.
    /// </summary>
    protected virtual AppHostEnvironmentRequirement RequiredEnvironment =>
        AppHostEnvironmentRequirement.OptIn | AppHostEnvironmentRequirement.Docker;

    /// <summary>The startup and readiness budgets. Defaults to <see cref="AppHostReadinessBudget.Default"/>.</summary>
    protected virtual AppHostReadinessBudget Budget => AppHostReadinessBudget.Default;

    /// <summary>
    /// Whether to mint an ephemeral RS256 keypair into the process environment when the
    /// <c>E2E_JWT_*</c> variables are absent. On by default: an AppHost that calls
    /// <c>WithE2eRsaKeys()</c> forwards them to its Identity resource, and without key material that
    /// resource answers every request (liveness probe included) with a 500 and never turns healthy.
    /// Turn it off only when the host takes its keys from somewhere this fixture must not override.
    /// </summary>
    protected virtual bool SuppliesE2eRsaKeys => true;

    /// <summary>
    /// Which resources to wait for, by name. <see langword="null"/> (the default) means every project
    /// resource in the model: containers are already awaited transitively through the WaitFor edges
    /// the projects declare, and naming them again only lengthens the critical path.
    /// </summary>
    protected virtual IReadOnlyCollection<string>? ResourcesToAwait => null;

    /// <summary>
    /// Boots the AppHost, unless a precondition is missing.
    /// </summary>
    /// <returns>A task that completes once the awaited resources are ready.</returns>
    public async ValueTask InitializeAsync()
    {
        SkipReason = AppHostEnvironmentGate.Evaluate(RequiredEnvironment);
        if (SkipReason is not null)
        {
            return;
        }

        var budget = Budget;
        budget.Validate();

        if (SuppliesE2eRsaKeys)
        {
            EnsureE2eRsaKeys();
        }

        using var startup = new CancellationTokenSource(budget.Startup);

        var builder = await CreateBuilderAsync(startup.Token).ConfigureAwait(false);
        ConfigureBuilder(builder);

        var application = await builder.BuildAsync(startup.Token).ConfigureAwait(false);
        _application = application;

        await application.StartAsync(startup.Token).ConfigureAwait(false);
        await WaitForResourcesAsync(application, budget).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops and disposes the application, then restores every environment variable this fixture
    /// pushed.
    /// </summary>
    /// <returns>A task that completes once the stack is down.</returns>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_application is not null)
        {
            try
            {
                await _application.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A resource that refuses to stop must not turn a green collection red on teardown.
            }

            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
        }

        foreach (var entry in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }

        _originalEnvironment.Clear();
    }

    /// <summary>
    /// Creates the testing builder for the AppHost under test. Implemented by
    /// <see cref="AppHostFixtureBase{TAppHost}"/>; a fixture only overrides this to build the host
    /// some other way.
    /// </summary>
    /// <param name="cancellationToken">The startup budget.</param>
    /// <returns>The testing builder.</returns>
    protected abstract Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Hook for test-only configuration, run after the builder exists and before the application is
    /// built. This is where a fixture pushes keys such as <c>Outbox:PollingIntervalSeconds</c> onto
    /// <c>builder.Configuration</c> so a test does not wait a production poll interval, or replaces a
    /// service registration. The default does nothing.
    /// </summary>
    /// <param name="builder">The testing builder.</param>
    protected virtual void ConfigureBuilder(IDistributedApplicationTestingBuilder builder)
    {
    }

    /// <summary>
    /// Hook for the options the AppHost itself is created with. The defaults switch the dashboard off
    /// (nothing looks at it in a test run, and it binds a port that a parallel run would collide on)
    /// and allow unsecured transport (a test stack usually declares cleartext endpoints only, which
    /// the AppHost otherwise refuses).
    /// </summary>
    /// <param name="options">The distributed application options.</param>
    /// <param name="settings">The host application builder settings.</param>
    protected virtual void ConfigureOptions(DistributedApplicationOptions options, HostApplicationBuilderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.DisableDashboard = true;
        options.AllowUnsecuredTransport = true;
    }

    /// <summary>
    /// Sets a process environment variable, snapshotting its ORIGINAL value for
    /// <see cref="DisposeAsync"/> to restore. The AppHost is built in this process, so its own
    /// environment is what <c>WithE2eRsaKeys()</c> and any other environment-reading extension sees.
    /// </summary>
    /// <param name="key">The environment variable name.</param>
    /// <param name="value">The value to set, or <see langword="null"/> to clear it.</param>
    protected void SetEnvironmentVariable(string key, string? value)
    {
        if (!_originalEnvironment.ContainsKey(key))
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
        }

        Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>
    /// Mints a keypair into the process environment unless BOTH variables already carry one. Both,
    /// not either: a half-set pair is worse than none, because the host would validate against a
    /// public key nothing signed with.
    /// </summary>
    private void EnsureE2eRsaKeys()
    {
        var existingPrivateKey = Environment.GetEnvironmentVariable(EphemeralRsaKeyPair.PrivateKeyVariable);
        var existingPublicKey = Environment.GetEnvironmentVariable(EphemeralRsaKeyPair.PublicKeyVariable);
        if (!string.IsNullOrWhiteSpace(existingPrivateKey) && !string.IsNullOrWhiteSpace(existingPublicKey))
        {
            E2eRsaKeys = new EphemeralRsaKeyPair(existingPrivateKey, existingPublicKey);
            return;
        }

        var keys = EphemeralRsaKeyPair.Create();
        SetEnvironmentVariable(EphemeralRsaKeyPair.PrivateKeyVariable, keys.PrivateKeyPem);
        SetEnvironmentVariable(EphemeralRsaKeyPair.PublicKeyVariable, keys.PublicKeyPem);
        E2eRsaKeys = keys;
    }

    /// <summary>
    /// Waits for each awaited resource inside one shared readiness budget, healthy where the resource
    /// carries a health check and Running where it does not.
    /// </summary>
    /// <param name="application">The started application.</param>
    /// <param name="budget">The budgets.</param>
    private async Task WaitForResourcesAsync(DistributedApplication application, AppHostReadinessBudget budget)
    {
        var resources = ResolveResources(application);
        var notifications = application.ResourceNotifications;
        var elapsed = Stopwatch.StartNew();

        foreach (var resource in resources)
        {
            var remaining = budget.Remaining(elapsed.Elapsed);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The readiness budget of {budget.Readiness} ran out before resource '{resource.Name}' was awaited.");
            }

            var isHealthChecked = resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out _);
            using var wait = new CancellationTokenSource(remaining);

            try
            {
                if (isHealthChecked)
                {
                    await notifications.WaitForResourceHealthyAsync(resource.Name, wait.Token).ConfigureAwait(false);
                }
                else
                {
                    await notifications
                        .WaitForResourceAsync(resource.Name, KnownResourceStates.Running, wait.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException exception)
            {
                var expected = isHealthChecked ? "healthy" : KnownResourceStates.Running;
                throw new TimeoutException(
                    $"Resource '{resource.Name}' did not become {expected} within the {budget.Readiness} readiness budget.",
                    exception);
            }
        }
    }

    /// <summary>
    /// The resources to await: the named ones, or every project resource when none are named.
    /// </summary>
    /// <param name="application">The started application.</param>
    /// <returns>The resources, in model order.</returns>
    /// <exception cref="InvalidOperationException">A named resource is not in the model.</exception>
    private IReadOnlyList<IResource> ResolveResources(DistributedApplication application)
    {
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        var names = ResourcesToAwait;

        if (names is null)
        {
            return [.. model.Resources.OfType<ProjectResource>()];
        }

        return
        [
            .. names.Select(name =>
                model.Resources.FirstOrDefault(resource => string.Equals(resource.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"ResourcesToAwait names '{name}', which is not a resource in this AppHost.")),
        ];
    }
}
