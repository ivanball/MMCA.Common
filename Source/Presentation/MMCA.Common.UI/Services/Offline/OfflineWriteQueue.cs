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
/// The queue starts a replay, without awaiting it, whenever the device comes back online. Signing out
/// clears the local store (<see cref="ILocalCacheStore.ClearAsync"/>), and with it the queue.
/// </para>
/// </remarks>
public sealed partial class OfflineWriteQueue : IOfflineWriteQueue, IDisposable
{
    private readonly ILocalCacheStore _store;
    private readonly IConnectivityStatusService _connectivity;
    private readonly Dictionary<string, IOfflineWriteHandler> _handlers = [with(StringComparer.Ordinal)];
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly TimeProvider _timeProvider;
    private readonly OfflineWriteQueueOptions _options;
    private readonly ILogger<OfflineWriteQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _replaying;
    private bool _disposed;

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

        try
        {
            return await ReplayPassAsync(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _replaying, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

    private async Task<int> ReplayPassAsync(CancellationToken cancellationToken)
    {
        var scope = await GetUserScopeAsync();
        var snapshot = InScope(await ReadAsync(cancellationToken), scope).ToList();
        var now = _timeProvider.GetUtcNow();
        var settled = 0;

        foreach (var write in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                break;
            }

            if (Classify(result) is not { } outcome)
            {
                await CountAttemptAsync(write, cancellationToken);
                break;
            }

            await SettleAsync(write, outcome, result, cancellationToken);
            settled++;
        }

        return settled;
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
        if (_disposed || !_connectivity.IsOnline)
        {
            return;
        }

        _ = ReplayInBackgroundAsync();
    }

    private async Task ReplayInBackgroundAsync()
    {
        try
        {
            await ReplayAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A queue disposed while the replay ran (the scope ended) has nobody left to tell.
            if (!_disposed)
            {
                LogBackgroundReplayFailed(exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline write replay handler for {Kind} failed on entry {WriteId}; the entry is kept for a later replay")]
    private partial void LogHandlerFailed(Exception exception, string kind, Guid writeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline write replay started on reconnect failed")]
    private partial void LogBackgroundReplayFailed(Exception exception);
}
