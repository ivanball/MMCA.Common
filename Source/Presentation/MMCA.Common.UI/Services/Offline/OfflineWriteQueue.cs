using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Services.Offline;

/// <summary>
/// The <see cref="IOfflineWriteQueue"/> over the device's <see cref="ILocalCacheStore"/>. The whole
/// queue is one <see cref="List{T}"/> under <see cref="OfflineWriteQueueOptions.StorageKey"/>; every
/// change re-reads, edits and writes it back under one semaphore, so concurrent callers never lose a
/// write. Entries are scoped to the signed-in user read from the <see cref="AuthenticationStateProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// A replay walks the current user's entries oldest first. An entry older than
/// <see cref="OfflineWriteQueueOptions.MaxAge"/> is discarded as <see cref="OfflineWriteOutcome.Expired"/>
/// without being sent; an entry whose kind has no handler is skipped and kept. A handler result that
/// is a transport failure, a timeout, or an unexpected, too-many-requests or unauthorized error keeps
/// the entry with one more attempt and stops the pass; any other conflict discards it as
/// <see cref="OfflineWriteOutcome.Conflict"/>, and any other failure as <see cref="OfflineWriteOutcome.Rejected"/>.
/// </para>
/// <para>
/// A replay runs in three situations: the queue starts one, without awaiting it, whenever the device
/// comes back online; <c>OfflineWriteNotifier</c> runs one on its first interactive render; and the
/// queue retries by itself while the current user has a write it can replay and the device reports
/// online. That retry is one timer on the injected <see cref="TimeProvider"/>, never more than one:
/// it is set <see cref="InitialRetryDelay"/> after a write with a registered handler is queued online,
/// and after any pass that leaves such a write pending, either because the pass stopped on a transient
/// failure or faulted (the delay doubles after each pass that settled nothing, up to
/// <see cref="MaxRetryDelay"/>, and returns to <see cref="InitialRetryDelay"/> after a pass that
/// settled a write) or because a write was queued while the pass ran. A pass that leaves nothing to
/// retry, the device going offline, and <see cref="Dispose"/> remove the timer, so an empty queue or an
/// offline device holds none. A retry is refused like any other replay while one is running.
/// </para>
/// <para>
/// Signing out clears the local store (<see cref="ILocalCacheStore.ClearAsync"/>), and with it the queue.
/// </para>
/// </remarks>
public sealed partial class OfflineWriteQueue : IOfflineWriteQueue, IDisposable
{
    /// <summary>The delay of the first scheduled retry, and of the next one after a pass that settled a write.</summary>
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The longest delay between scheduled retries; the doubling stops here.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ILocalCacheStore _store;
    private readonly IConnectivityStatusService _connectivity;
    private readonly Dictionary<string, IOfflineWriteHandler> _handlers = [with(StringComparer.Ordinal)];
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly TimeProvider _timeProvider;
    private readonly OfflineWriteQueueOptions _options;
    private readonly ILogger<OfflineWriteQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _scheduleSync = new();
    private int _replaying;
    private long _replayableEnqueues;
    private bool _disposed;
    private ITimer? _retryTimer;
    private object? _retryToken;
    private TimeSpan _retryDelay = InitialRetryDelay;

    /// <summary>Initializes a new instance of the <see cref="OfflineWriteQueue"/> class and subscribes to connectivity changes.</summary>
    /// <param name="store">The device's local store.</param>
    /// <param name="connectivity">The device's connectivity status.</param>
    /// <param name="handlers">The registered replay handlers; the first one registered for a kind wins.</param>
    /// <param name="authenticationStateProvider">Supplies the signed-in user the entries are scoped to.</param>
    /// <param name="timeProvider">Stamps and ages the entries.</param>
    /// <param name="options">The queue options.</param>
    /// <param name="logger">Logs handler and background replay faults.</param>
    public OfflineWriteQueue(
        ILocalCacheStore store,
        IConnectivityStatusService connectivity,
        IEnumerable<IOfflineWriteHandler> handlers,
        AuthenticationStateProvider authenticationStateProvider,
        TimeProvider timeProvider,
        IOptions<OfflineWriteQueueOptions> options,
        ILogger<OfflineWriteQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(options);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _authenticationStateProvider = authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;

        foreach (var handler in handlers)
        {
            _handlers.TryAdd(handler.Kind, handler);
        }

        _connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    /// <inheritdoc />
    public event EventHandler<OfflineWriteSettledEventArgs>? WriteSettled;

    /// <inheritdoc />
    public event EventHandler? PendingChanged;

    /// <inheritdoc />
    public bool IsAvailable => _store.IsAvailable;

    /// <summary>
    /// Gets the replay the queue last started by itself (on reconnect or from the retry timer), which
    /// never faults; completed when none was started. Lets tests wait for a background pass to finish.
    /// </summary>
    internal Task BackgroundReplay { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public async Task<Result<PendingWrite>> EnqueueAsync<TPayload>(
        string kind,
        TPayload payload,
        OfflineWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (!IsAvailable)
        {
            return Result.Failure<PendingWrite>(Error.Failure(
                "OfflineWrites.Unavailable", "This device cannot keep writes for later."));
        }

        var write = new PendingWrite
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            IdempotencyKey = options?.IdempotencyKey ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            BasedOnRowVersion = options?.BasedOnRowVersion,
            PayloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            CoalesceKey = options?.CoalesceKey,
            UserScope = await GetUserScopeAsync(),
            EnqueuedOn = _timeProvider.GetUtcNow(),
            Attempts = 0,
        };

        var added = await MutateAsync(
            entries =>
            {
                if (entries.Count >= _options.MaxPendingWrites)
                {
                    return false;
                }

                entries.Add(write);
                return true;
            },
            cancellationToken);

        if (added && _handlers.ContainsKey(kind))
        {
            // Counted before the schedule check, so a pass that read the store before this write was
            // saved sees the count move and keeps a retry scheduled (CompletePass).
            Interlocked.Increment(ref _replayableEnqueues);
            lock (_scheduleSync)
            {
                ScheduleRetryLocked();
            }
        }

        return added
            ? Result.Success(write)
            : Result.Failure<PendingWrite>(Error.Failure(
                "OfflineWrites.QueueFull", "Too many changes are waiting to be sent from this device."));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingWrite>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return [];
        }

        var scope = await GetUserScopeAsync();
        var entries = await ReadAsync(cancellationToken);
        return [.. InScope(entries, scope)];
    }

    /// <inheritdoc />
    public async Task<bool> CancelPendingAsync(string coalesceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coalesceKey);

        if (!IsAvailable)
        {
            return false;
        }

        var scope = await GetUserScopeAsync();
        return await MutateAsync(
            entries => entries.RemoveAll(w =>
                string.Equals(w.UserScope, scope, StringComparison.Ordinal)
                && string.Equals(w.CoalesceKey, coalesceKey, StringComparison.Ordinal)) > 0,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ReplayAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_connectivity.IsOnline || Interlocked.CompareExchange(ref _replaying, 1, 0) != 0)
        {
            return 0;
        }

        var enqueuesBefore = Interlocked.Read(ref _replayableEnqueues);
        var settled = 0;
        var retry = true;
        try
        {
            (settled, var stoppedOnTransient) = await ReplayPassAsync(cancellationToken);
            retry = stoppedOnTransient;
            return settled;
        }
        finally
        {
            // A pass that threw (a store fault, a cancellation) leaves its writes pending, so it counts
            // as a pass that settled nothing and still schedules the next attempt.
            CompletePass(settled, retry, enqueuesBefore);
            Volatile.Write(ref _replaying, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_scheduleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelRetryLocked();
        }

        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
        _gate.Dispose();
    }

    private static IEnumerable<PendingWrite> InScope(List<PendingWrite> entries, string? scope) =>
        entries
            .Where(w => string.Equals(w.UserScope, scope, StringComparison.Ordinal))
            .OrderBy(w => w.EnqueuedOn);

    /// <summary>
    /// Sorts a handler result: <see langword="null"/> for a transient failure the entry is kept for,
    /// otherwise the outcome it settles with. The transient check runs before the conflict check.
    /// </summary>
    private static OfflineWriteOutcome? Classify(Result result)
    {
        if (result.IsSuccess)
        {
            return OfflineWriteOutcome.Completed;
        }

        if (result.Errors.Any(IsTransient))
        {
            return null;
        }

        return result.Errors.Any(error => error.Type == ErrorType.Conflict)
            ? OfflineWriteOutcome.Conflict
            : OfflineWriteOutcome.Rejected;
    }

    private static bool IsTransient(Error error) =>
        string.Equals(error.Code, HttpResultExecutor.TransportErrorCode, StringComparison.Ordinal)
        || string.Equals(error.Code, HttpResultExecutor.TimeoutErrorCode, StringComparison.Ordinal)
        || error.Type is ErrorType.Unexpected or ErrorType.TooManyRequests or ErrorType.Unauthorized;

    private static TimeSpan NextDelay(TimeSpan delay) =>
        delay >= MaxRetryDelay / 2 ? MaxRetryDelay : delay * 2;

    /// <summary>
    /// Walks the current scope's entries once, skipping any that left the store since the pass read it.
    /// Returns how many settled and whether the pass stopped on a transient failure, which leaves that
    /// entry (and the ones after it) pending.
    /// </summary>
    private async Task<(int Settled, bool StoppedOnTransient)> ReplayPassAsync(CancellationToken cancellationToken)
    {
        var scope = await GetUserScopeAsync();
        var snapshot = InScope(await ReadAsync(cancellationToken), scope).ToList();
        var now = _timeProvider.GetUtcNow();
        var settled = 0;

        foreach (var write in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The snapshot is as old as the handlers before this entry took to answer. A write
            // cancelled since then (CancelPendingAsync) is no longer wanted and must not be sent; a
            // write cancelled while its own request is in flight still lands.
            if (!await IsStillPendingAsync(write.Id, cancellationToken))
            {
                continue;
            }

            if (now - write.EnqueuedOn > _options.MaxAge)
            {
                await SettleAsync(write, OfflineWriteOutcome.Expired, result: null, cancellationToken);
                settled++;
                continue;
            }

            if (!_handlers.TryGetValue(write.Kind, out var handler))
            {
                // No handler for this kind in this app: keep it for one that has one, and go on.
                continue;
            }

            Result result;
            try
            {
                result = await handler.ReplayAsync(write, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogHandlerFailed(exception, write.Kind, write.Id);
                await CountAttemptAsync(write, cancellationToken);
                return (settled, true);
            }

            if (Classify(result) is not { } outcome)
            {
                await CountAttemptAsync(write, cancellationToken);
                return (settled, true);
            }

            await SettleAsync(write, outcome, result, cancellationToken);
            settled++;
        }

        return (settled, false);
    }

    /// <summary>
    /// Sets or removes the retry timer after a pass that ran. <paramref name="retry"/> is true when
    /// the pass stopped on a transient failure or threw; otherwise a retry is kept only when a
    /// replayable write was queued after the pass started, since the pass may not have seen it.
    /// </summary>
    private void CompletePass(int settled, bool retry, long enqueuesBefore)
    {
        lock (_scheduleSync)
        {
            if (retry)
            {
                _retryDelay = settled > 0 ? InitialRetryDelay : NextDelay(_retryDelay);
                ScheduleRetryLocked();
            }
            else if (Interlocked.Read(ref _replayableEnqueues) != enqueuesBefore)
            {
                _retryDelay = InitialRetryDelay;
                ScheduleRetryLocked();
            }
            else
            {
                CancelRetryLocked();
            }
        }
    }

    /// <summary>Creates the one retry timer unless it exists, the queue is disposed or the device is offline. Caller holds <see cref="_scheduleSync"/>.</summary>
    private void ScheduleRetryLocked()
    {
        if (_disposed || _retryTimer is not null || !_connectivity.IsOnline)
        {
            return;
        }

        // The token tells this timer's callback apart from one a later schedule replaced.
        var token = new object();
        _retryToken = token;
        _retryTimer = _timeProvider.CreateTimer(OnRetryTimer, token, _retryDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Removes the retry timer and resets the backoff. Caller holds <see cref="_scheduleSync"/>.</summary>
    private void CancelRetryLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        _retryToken = null;
        _retryDelay = InitialRetryDelay;
    }

    private void OnRetryTimer(object? state)
    {
        lock (_scheduleSync)
        {
            if (_disposed || state is null || !ReferenceEquals(state, _retryToken))
            {
                return;
            }

            // One-shot: the timer is spent. The pass it starts schedules the next one if needed, and a
            // pass already running refuses this one and schedules on its own completion.
            _retryTimer?.Dispose();
            _retryTimer = null;
            _retryToken = null;
        }

        BackgroundReplay = ReplayInBackgroundAsync(fromRetryTimer: true);
    }

    private async Task SettleAsync(PendingWrite write, OfflineWriteOutcome outcome, Result? result, CancellationToken cancellationToken)
    {
        await MutateAsync(entries => entries.RemoveAll(w => w.Id == write.Id) > 0, cancellationToken);
        WriteSettled?.Invoke(this, new OfflineWriteSettledEventArgs(write, outcome, result));
    }

    private Task<bool> CountAttemptAsync(PendingWrite write, CancellationToken cancellationToken) =>
        MutateAsync(
            entries =>
            {
                var index = entries.FindIndex(w => w.Id == write.Id);
                if (index < 0)
                {
                    return false;
                }

                entries[index] = entries[index] with { Attempts = entries[index].Attempts + 1 };
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Re-reads the list, applies <paramref name="edit"/> and, when it reports a change, writes the
    /// list back and raises <see cref="PendingChanged"/>, all under the one semaphore.
    /// </summary>
    private async Task<bool> MutateAsync(Func<List<PendingWrite>, bool> edit, CancellationToken cancellationToken)
    {
        bool changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAsync(cancellationToken);
            changed = edit(entries);
            if (changed)
            {
                await _store.SetAsync(_options.StorageKey, entries, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>Whether the store still holds the write, read under the one semaphore.</summary>
    private async Task<bool> IsStillPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).Exists(w => w.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PendingWrite>> ReadAsync(CancellationToken cancellationToken) =>
        await _store.GetAsync<List<PendingWrite>>(_options.StorageKey, cancellationToken) ?? [];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0013", Justification = "The queue reads the authentication state afresh at every operation and caches nothing, so there is no state for the AuthenticationStateChanged event to refresh.")]
    private async Task<string?> GetUserScopeAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.GetUserId() is { } userId
            ? string.Create(CultureInfo.InvariantCulture, $"{userId}")
            : null;
    }

    private void OnConnectivityChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_connectivity.IsOnline)
        {
            // Offline: no retry until the change back to online, which replays at once.
            lock (_scheduleSync)
            {
                CancelRetryLocked();
            }

            return;
        }

        BackgroundReplay = ReplayInBackgroundAsync(fromRetryTimer: false);
    }

    private async Task ReplayInBackgroundAsync(bool fromRetryTimer)
    {
        try
        {
            await ReplayAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A queue disposed while the replay ran (the scope ended) has nobody left to tell.
            if (_disposed)
            {
                return;
            }

            if (fromRetryTimer)
            {
                LogScheduledReplayFailed(exception);
            }
            else
            {
                LogBackgroundReplayFailed(exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline write replay handler for {Kind} failed on entry {WriteId}; the entry is kept for a later replay")]
    private partial void LogHandlerFailed(Exception exception, string kind, Guid writeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline write replay started on reconnect failed")]
    private partial void LogBackgroundReplayFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline write replay started by the retry schedule failed; the next attempt stays scheduled")]
    private partial void LogScheduledReplayFailed(Exception exception);
}
