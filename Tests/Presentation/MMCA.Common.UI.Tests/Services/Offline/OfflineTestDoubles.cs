using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Offline;

namespace MMCA.Common.UI.Tests.Services.Offline;

/// <summary>
/// In-memory <see cref="ILocalCacheStore"/> for the offline write queue tests. Every value is JSON
/// round-tripped with the default serializer options, exactly as <c>BrowserLocalCacheStore</c>
/// does, so the queue's persisted shape is genuinely exercised and a second queue over the same
/// store sees what the first wrote. Each call yields before touching the dictionary so concurrent
/// callers genuinely interleave. Reads and writes are counted so a test can prove the store was
/// never touched.
/// </summary>
internal sealed class InMemoryLocalCacheStore : ILocalCacheStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private int _reads;
    private int _writes;

    public bool IsAvailable { get; set; } = true;

    /// <summary>When true, every read throws, standing in for a store that faults.</summary>
    public bool ThrowOnRead { get; set; }

    public int ReadCount => Volatile.Read(ref _reads);

    public int WriteCount => Volatile.Read(ref _writes);

    public IReadOnlyCollection<string> Keys => [.. _entries.Keys];

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Interlocked.Increment(ref _writes);
        _entries[key] = JsonSerializer.Serialize(value);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Interlocked.Increment(ref _reads);
        if (ThrowOnRead)
        {
            throw new InvalidOperationException("The probe store faulted.");
        }

        return _entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Interlocked.Increment(ref _writes);
        _entries.TryRemove(key, out _);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        Interlocked.Increment(ref _writes);
        _entries.Clear();
    }

    /// <summary>Writes a value directly, bypassing the counters (test arrangement).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    public void Seed<T>(string key, T value) => _entries[key] = JsonSerializer.Serialize(value);

    /// <summary>Reads a value directly, bypassing the counters (test assertion).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    public T? Peek<T>(string key) =>
        _entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
}

/// <summary>
/// <see cref="IConnectivityStatusService"/> whose state a test flips; <see cref="SetOnline"/>
/// raises <see cref="ConnectivityChanged"/> synchronously and <see cref="SubscriberCount"/> exposes
/// how many handlers are attached, so subscription and unsubscription are observable.
/// </summary>
internal sealed class ControllableConnectivityService(bool isOnline = true) : IConnectivityStatusService
{
    private readonly Lock _sync = new();
    private EventHandler? _changed;

    public event EventHandler? ConnectivityChanged
    {
        add
        {
            lock (_sync)
            {
                _changed += value;
            }
        }

        remove
        {
            lock (_sync)
            {
                _changed -= value;
            }
        }
    }

    public bool IsOnline { get; private set; } = isOnline;

    public int SubscriberCount
    {
        get
        {
            lock (_sync)
            {
                return _changed?.GetInvocationList().Length ?? 0;
            }
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void SetOnline(bool online)
    {
        IsOnline = online;
        EventHandler? handlers;
        lock (_sync)
        {
            handlers = _changed;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// <see cref="AuthenticationStateProvider"/> whose principal a test switches between users, so the
/// queue's per-user scoping can be exercised over one shared store.
/// </summary>
internal sealed class SwitchableAuthenticationStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _principal;

    private SwitchableAuthenticationStateProvider(ClaimsPrincipal principal) => _principal = principal;

    public static SwitchableAuthenticationStateProvider SignedInAs(string userId) =>
        new(TestPrincipal.AuthenticatedUser(userId));

    public void SignInAs(string userId) => _principal = TestPrincipal.AuthenticatedUser(userId);

    public void SignOut() => _principal = new ClaimsPrincipal(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(_principal));
}

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="FakeTimeProvider"/> that records every timer the
/// queue creates (its due time) and how many are still alive (created and not disposed), so a test
/// can prove the retry schedule holds at most one timer, none while idle, and none after disposal.
/// Time moves only through <see cref="Advance"/>, which runs due callbacks on the calling thread.
/// </summary>
internal sealed class CountingTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _dueTimes = new();
    private int _active;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    /// <summary>Gets the due time of every timer created so far, in creation order.</summary>
    public IReadOnlyList<TimeSpan> DueTimes => [.. _dueTimes];

    /// <summary>Gets how many created timers have not been disposed.</summary>
    public int ActiveTimers => Volatile.Read(ref _active);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public void Advance(TimeSpan delta) => inner.Advance(delta);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _dueTimes.Enqueue(dueTime);
        Interlocked.Increment(ref _active);
        return new CountedTimer(inner.CreateTimer(callback, state, dueTime, period), this);
    }

    private sealed class CountedTimer(ITimer timer, CountingTimeProvider owner) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._active);
            }

            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A consumer-side <see cref="IOfflineWriteHandler"/> whose replay outcome each test scripts, and
/// which records every write it was asked to replay, in order.
/// </summary>
internal sealed class ScriptedWriteHandler(string kind, Func<PendingWrite, CancellationToken, Task<Result>> replay)
    : IOfflineWriteHandler
{
    private readonly ConcurrentQueue<PendingWrite> _calls = new();

    public string Kind => kind;

    public IReadOnlyList<PendingWrite> Calls => [.. _calls];

    public static ScriptedWriteHandler Returning(string kind, Result result) =>
        new(kind, (_, _) => Task.FromResult(result));

    public Task<Result> ReplayAsync(PendingWrite write, CancellationToken cancellationToken)
    {
        _calls.Enqueue(write);
        return replay(write, cancellationToken);
    }
}
