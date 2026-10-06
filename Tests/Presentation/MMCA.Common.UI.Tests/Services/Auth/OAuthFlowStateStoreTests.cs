using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Covers <see cref="OAuthFlowStateStore"/> (SEC-Common-85): the binding that stops a completion
/// code minted by someone else's provider round trip from being exchanged just because the URL was
/// deep-linked into this app. The attacker case is the one that matters: a completion arriving with
/// no attempt started here must be dropped, not redeemed.
/// </summary>
public sealed class OAuthFlowStateStoreTests
{
    private readonly FakeCacheStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TryCompleteAsync_WithNoAttemptStartedOnThisClient_Refuses()
    {
        var sut = new OAuthFlowStateStore(_store, _time);

        var allowed = await sut.TryCompleteAsync("someone-elses-state", TestContext.Current.CancellationToken);

        allowed.Should().BeFalse(
            "a code that arrives without a flow this client started is the forced-sign-in attack");
    }

    [Fact]
    public async Task TryCompleteAsync_WithTheValueThisClientMinted_Allows()
    {
        var sut = new OAuthFlowStateStore(_store, _time);
        var state = await sut.BeginAsync(TestContext.Current.CancellationToken);

        var allowed = await sut.TryCompleteAsync(state, TestContext.Current.CancellationToken);

        state.Should().NotBeNullOrWhiteSpace();
        allowed.Should().BeTrue("the ordinary sign-in must keep working");
    }

    [Fact]
    public async Task TryCompleteAsync_WithADifferentValue_Refuses()
    {
        var sut = new OAuthFlowStateStore(_store, _time);
        await sut.BeginAsync(TestContext.Current.CancellationToken);

        var allowed = await sut.TryCompleteAsync("not-the-value-we-minted", TestContext.Current.CancellationToken);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task TryCompleteAsync_WithAPendingAttemptButNoReturnedState_Refuses()
    {
        // M127: omitting the parameter must not bypass the binding.
        var sut = new OAuthFlowStateStore(_store, _time);

        await sut.BeginAsync(TestContext.Current.CancellationToken);
        var allowedWithNull = await sut.TryCompleteAsync(null, TestContext.Current.CancellationToken);

        await sut.BeginAsync(TestContext.Current.CancellationToken);
        var allowedWithEmpty = await sut.TryCompleteAsync(string.Empty, TestContext.Current.CancellationToken);

        allowedWithNull.Should().BeFalse("every legitimate flow round-trips the state");
        allowedWithEmpty.Should().BeFalse("an empty value carries no binding either");
    }

    [Fact]
    public async Task TryCompleteAsync_ConsumesTheAttempt_SoAValueIsGoodOnlyOnce()
    {
        var sut = new OAuthFlowStateStore(_store, _time);
        var state = await sut.BeginAsync(TestContext.Current.CancellationToken);

        (await sut.TryCompleteAsync(state, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await sut.TryCompleteAsync(state, TestContext.Current.CancellationToken)).Should().BeFalse(
            "a replayed completion must not find a live attempt waiting for it");
    }

    [Fact]
    public async Task TryCompleteAsync_AfterTheAttemptExpires_Refuses()
    {
        var sut = new OAuthFlowStateStore(_store, _time);
        var state = await sut.BeginAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(11));

        var allowed = await sut.TryCompleteAsync(state, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse("an abandoned attempt must not stay redeemable indefinitely");
    }

    [Fact]
    public async Task BeginAsync_MintsADifferentValueEveryTime()
    {
        var sut = new OAuthFlowStateStore(_store, _time);

        var first = await sut.BeginAsync(TestContext.Current.CancellationToken);
        var second = await sut.BeginAsync(TestContext.Current.CancellationToken);

        second.Should().NotBe(first, "a predictable value would be forgeable by the attacker");
    }

    [Fact]
    public async Task OnAHostWithNoDurableStorage_TheBindingIsNotEnforced()
    {
        var sut = new OAuthFlowStateStore(new FakeCacheStore { IsAvailable = false }, _time);

        sut.IsEnforced.Should().BeFalse();
        (await sut.BeginAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.TryCompleteAsync(null, TestContext.Current.CancellationToken)).Should().BeTrue(
            "nothing durable can be written across the redirect there, so sign-in keeps its old behaviour");
    }

    // M188: a store that reports itself available but drops the write (quota, blocked site data,
    // private mode) must be caught BEFORE the redirect, not one redirect too late at completion.
    [Fact]
    public async Task BeginAsync_WhenTheStoreDropsTheWrite_ThrowsInsteadOfArmingAnUnpersistedAttempt()
    {
        var sut = new OAuthFlowStateStore(new DroppingCacheStore(), _time);

        Func<Task> act = () => sut.BeginAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class DroppingCacheStore : ILocalCacheStore
    {
        public bool IsAvailable => true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCacheStore : ILocalCacheStore
    {
        private readonly Dictionary<string, string> _entries = [];

        public bool IsAvailable { get; init; } = true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _entries[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }
}
