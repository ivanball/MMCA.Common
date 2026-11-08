using AwesomeAssertions;
using Microsoft.FeatureManagement.FeatureFilters;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.Tests.FeatureFlags;

/// <summary>
/// The approved assertions for reloadable feature flags (spec section E), against a host opted in with
/// <c>AddReloadableFeatureFlags</c> over a real SQLite database and the two-level cache:
/// <list type="number">
/// <item>with no override, the configured value is used;</item>
/// <item>an override wins, and its clear restores config;</item>
/// <item>an undeclared flag name is refused;</item>
/// <item>the snapshot is invalidated on write;</item>
/// <item>the FeatureGate decorator honors an override with no restart.</item>
/// </list>
/// Every flag answer is read through <c>IFeatureManager</c>, the one interface the FeatureGate
/// decorators call, so nothing here depends on how the override is layered underneath.
/// </summary>
public sealed class ReloadableFeatureFlagsTests
{
    private const string Reason = "Incident 4711: payment provider outage";

    // ── 1. With no override, the configured value is used ──
    [Fact]
    public async Task WithNoOverride_TheConfiguredValueIsUsed()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeTrue();
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeFalse();
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.NotConfigured)).Should().BeFalse(
            "a declared flag configuration never mentions is off, exactly as without the stored layer");

        var listed = await host.AdminAsync(admin => admin.ListAsync(CancellationToken.None));
        var on = listed.Value!.Single(flag => flag.Name == ReloadableTestFeatures.ConfiguredOn);
        on.Configured.Should().Be(FeatureFlagValueKind.On);
        on.Override.Should().BeNull();
        on.Effective.Should().Be(FeatureFlagValueKind.On);
        on.Owner.Should().Be("framework-tests");
        on.Lifetime.Should().Be(FeatureFlagLifetime.Permanent);
    }

    // ── 2. An override wins, and its clear restores config ──
    [Fact]
    public async Task AnOffOverride_WinsOverConfiguredOn_AndClearingItRestoresConfig()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        var set = await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, Reason),
            CancellationToken.None));

        set.IsSuccess.Should().BeTrue();
        set.Value!.Configured.Should().Be(FeatureFlagValueKind.On);
        set.Value.Effective.Should().Be(FeatureFlagValueKind.Off);
        set.Value.Override!.Reason.Should().Be(Reason);
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeFalse(
            "the stored override wins over configuration");

        var cleared = await host.AdminAsync(admin => admin.ClearOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn, CancellationToken.None));

        cleared.IsSuccess.Should().BeTrue();
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeTrue(
            "clearing the override puts configuration back in force");
        var listed = await host.AdminAsync(admin => admin.ListAsync(CancellationToken.None));
        listed.Value!.Single(flag => flag.Name == ReloadableTestFeatures.ConfiguredOn).Override.Should().BeNull();
    }

    [Fact]
    public async Task AnOnOverride_WinsOverConfiguredOff_AndClearingItRestoresConfig()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOff,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.On, Reason),
            CancellationToken.None));
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeTrue();

        await host.AdminAsync(admin => admin.ClearOverrideAsync(ReloadableTestFeatures.ConfiguredOff, CancellationToken.None));
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeFalse();
    }

    [Fact]
    public async Task ATargetingOverride_EnablesTheNamedUserOnly()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOff,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Targeting, Reason, Users: ["alice"], Groups: ["Beta"]),
            CancellationToken.None));

        host.Targeting.Current = new TargetingContext { UserId = "alice", Groups = [] };
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeTrue();

        host.Targeting.Current = new TargetingContext { UserId = "carol", Groups = ["Beta"] };
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeTrue(
            "a targeted group enables the flag for every member");

        host.Targeting.Current = new TargetingContext { UserId = "bob", Groups = [] };
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeFalse();
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(0, false)]
    public async Task APercentageOverride_RollsOutToThatShare(int percentage, bool expected)
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Percentage, Reason, Percentage: percentage),
            CancellationToken.None));

        host.Targeting.Current = new TargetingContext { UserId = "dave", Groups = [] };
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().Be(expected);
    }

    // ── 3. An undeclared flag name is refused ──
    [Theory]
    [InlineData("Reloadable.NeverDeclared")]
    [InlineData(ReloadableFlagsHost.ConfiguredButUndeclared)]
    public async Task AnUndeclaredFlagName_IsRefused_AndNothingIsStored(string name)
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        var set = await host.AdminAsync(admin => admin.SetOverrideAsync(
            name,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, Reason),
            CancellationToken.None));
        var clear = await host.AdminAsync(admin => admin.ClearOverrideAsync(name, CancellationToken.None));

        set.IsFailure.Should().BeTrue("only a flag a *Features class declares may be overridden");
        set.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlag.Undeclared");
        clear.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlag.Undeclared");
        (await host.ReadAllRowsAsync()).Should().BeEmpty();

        var listed = await host.AdminAsync(admin => admin.ListAsync(CancellationToken.None));
        listed.Value!.Select(flag => flag.Name).Should().BeEquivalentTo(
            [ReloadableTestFeatures.ConfiguredOff, ReloadableTestFeatures.ConfiguredOn, ReloadableTestFeatures.NotConfigured],
            "the list is the declared set, not whatever configuration happens to name");
    }

    // ── 4. The snapshot is invalidated on write ──

    /// <summary>
    /// The cache lifetime is an hour, so only an invalidation can make a read see a change. A row
    /// written behind the service's back stays invisible (the snapshot really is cached); the next
    /// write through the service, even for ANOTHER flag, drops the whole snapshot and both rows show.
    /// </summary>
    [Fact]
    public async Task TheSnapshotIsInvalidatedOnWrite()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync(overrideCacheSeconds: 3600);
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeTrue();

        await host.InsertBehindTheCachesBackAsync(ReloadableTestFeatures.ConfiguredOn, FeatureFlagOverrideMode.Off);
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeTrue(
            "nothing invalidated the cached snapshot, so the direct row is not seen yet");

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOff,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.On, Reason),
            CancellationToken.None));

        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeTrue(
            "the write invalidated the snapshot, so its own override is seen at once");
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeFalse(
            "the invalidation dropped the whole snapshot, so the reload also picked up the direct row");

        await host.AdminAsync(admin => admin.ClearOverrideAsync(ReloadableTestFeatures.ConfiguredOff, CancellationToken.None));
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOff)).Should().BeFalse(
            "a clear is a write and invalidates the snapshot too");
    }

    // ── 5. The FeatureGate decorator honors an override with no restart ──
    [Fact]
    public async Task TheFeatureGateDecorator_HonorsAnOverride_WithNoRestart()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();
        var handler = new CountingHandler();
        var decorator = new FeatureGateCommandDecorator<GatedCommand, Result>(handler, host.FeatureManager);
        var command = new GatedCommand(ReloadableTestFeatures.ConfiguredOff);

        var before = await decorator.HandleAsync(command, CancellationToken.None);
        before.Errors.Should().ContainSingle().Which.Code.Should().Be("Feature.Disabled");
        handler.Calls.Should().Be(0);

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOff,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.On, Reason),
            CancellationToken.None));

        var after = await decorator.HandleAsync(command, CancellationToken.None);
        after.IsSuccess.Should().BeTrue("the same decorator instance, in the same process, sees the override");
        handler.Calls.Should().Be(1);

        await host.AdminAsync(admin => admin.ClearOverrideAsync(ReloadableTestFeatures.ConfiguredOff, CancellationToken.None));
        (await decorator.HandleAsync(command, CancellationToken.None)).IsFailure.Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    // ── Writes are audited through the auditable entity base ──
    [Fact]
    public async Task SetClearAndSetAgain_WriteOneAuditedRow()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn, new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, Reason), CancellationToken.None));
        await host.AdminAsync(admin => admin.ClearOverrideAsync(ReloadableTestFeatures.ConfiguredOn, CancellationToken.None));

        var cleared = (await host.ReadAllRowsAsync()).Should().ContainSingle().Subject;
        cleared.IsDeleted.Should().BeTrue("a clear keeps the row so its audit stamps say who cleared it");
        cleared.DeletedBy.Should().Be(ReloadableFlagsHost.OperatorId);
        cleared.CreatedBy.Should().Be(ReloadableFlagsHost.OperatorId);

        await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn, new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, "Second outage"), CancellationToken.None));

        var revived = (await host.ReadAllRowsAsync()).Should().ContainSingle().Subject;
        revived.IsDeleted.Should().BeFalse();
        revived.Reason.Should().Be("Second outage");
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeFalse();
    }

    [Fact]
    public async Task AnOverrideWithoutAReason_IsRefused()
    {
        await using var host = await ReloadableFlagsHost.CreateAsync();

        var set = await host.AdminAsync(admin => admin.SetOverrideAsync(
            ReloadableTestFeatures.ConfiguredOn,
            new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, "   "),
            CancellationToken.None));

        set.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlagOverride.ReasonRequired");
        (await host.ReadAllRowsAsync()).Should().BeEmpty();
        (await host.FeatureManager.IsEnabledAsync(ReloadableTestFeatures.ConfiguredOn)).Should().BeTrue();
    }

    /// <summary>A command gated on the flag it names.</summary>
    /// <param name="FeatureName">The flag the command is gated on.</param>
    private sealed record GatedCommand(string FeatureName) : IFeatureGated;

    private sealed class CountingHandler : ICommandHandler<GatedCommand, Result>
    {
        public int Calls { get; private set; }

        public Task<Result> HandleAsync(GatedCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result.Success());
        }
    }
}
