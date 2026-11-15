using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Common;

/// <summary>
/// Persists prerender state only when it fits the circuit-start message. On a Blazor Server circuit the
/// browser sends every component's <see cref="PersistentComponentState"/> back inside the SignalR message
/// that starts the circuit, and a message over the hub's <c>MaximumReceiveMessageSize</c> (32 KB by
/// default) closes the connection: the page reconnects with a new circuit, sends the same state again,
/// and never becomes interactive. The only trace is an <c>InvalidDataException</c> logged at Debug.
/// </summary>
/// <remarks>
/// Use this for any state whose size depends on content (rows, DTOs, lists); a small fixed-size value
/// can go through <c>PersistAsJson</c> directly. Skipping is always safe: a component that finds nothing
/// to restore fetches on its interactive pass, exactly as it does when it never prerendered. Public so a
/// consumer page that persists its own content-sized state uses the same budget and serializes once.
/// </remarks>
public static class PrerenderStateBudget
{
    /// <summary>
    /// The largest UTF-8 JSON payload one key may persist: 14 KB, under half of SignalR's 32 KB default.
    /// The headroom is deliberate. Every component on the page persists into the same circuit-start
    /// message, and the state travels re-encoded: each value is base64 inside the state dictionary, and
    /// the dictionary is Data Protection protected and base64 encoded again, so each JSON byte costs about
    /// 16/9 of a byte on the wire. 14 KB of JSON therefore lands near 25 KB, leaving room for the other
    /// components' state, the protector's envelope and the hub framing.
    /// </summary>
    public const int MaxJsonBytes = 14 * 1024;

    // The options PersistAsJson and TryTakeFromJson use, so bytes persisted here restore through
    // TryTakeFromJson unchanged.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The largest UTF-8 JSON payload all keys persisted through this class into one
    /// <see cref="PersistentComponentState"/> may add up to: 16 KB, about 28 KB on the wire at the
    /// 16/9 re-encoding <see cref="MaxJsonBytes"/> describes. The per-key cap alone did not bound the
    /// circuit-start message: two components persisting just under it each (a catalog page and a grid
    /// beside it) reached 28.5 KB of the 32 KB limit (Store manual test Run 4, O8). A key that would
    /// take the running total past this budget is skipped, and its component fetches on the interactive
    /// pass, exactly as a key over <see cref="MaxJsonBytes"/> does.
    /// </summary>
    public const int MaxTotalJsonBytes = 16 * 1024;

    /// <summary>
    /// The JSON bytes persisted so far into each state instance. Keyed weakly by the state object, so a
    /// tally lives exactly as long as the scope that owns the state, and no DI registration is needed.
    /// </summary>
    private static readonly ConditionalWeakTable<PersistentComponentState, PersistedTally> Tallies = [];

    /// <summary>
    /// Serializes <paramref name="instance"/> once and persists that JSON under <paramref name="key"/>
    /// when it fits <paramref name="maxJsonBytes"/> and also fits what is left of
    /// <see cref="MaxTotalJsonBytes"/> for this <paramref name="state"/>; otherwise persists nothing.
    /// </summary>
    /// <remarks>
    /// The page-wide total counts only the keys persisted through this method; a small fixed-size value
    /// persisted with <c>PersistAsJson</c> directly is outside it, which the headroom between 28 KB and
    /// the 32 KB limit absorbs. A larger <paramref name="maxJsonBytes"/> does not raise the page-wide
    /// budget. Persisting a key the tally has already counted starts a new tally: one persistence pass
    /// cannot persist a key twice, so the repeat marks a later pass over the same state instance.
    /// </remarks>
    /// <typeparam name="TValue">The persisted type; restore it with <c>TryTakeFromJson&lt;TValue&gt;</c>.</typeparam>
    /// <param name="state">The component's persistent state.</param>
    /// <param name="key">The persistence key.</param>
    /// <param name="instance">The value to persist.</param>
    /// <param name="maxJsonBytes">The budget in UTF-8 JSON bytes; defaults to <see cref="MaxJsonBytes"/>.</param>
    /// <returns><see langword="true"/> when the value was persisted; <see langword="false"/> when it was over budget.</returns>
    public static bool TryPersistAsJsonWithinBudget<TValue>(
        this PersistentComponentState state,
        string key,
        TValue instance,
        int maxJsonBytes = MaxJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(state);

        var json = JsonSerializer.SerializeToUtf8Bytes(instance, SerializerOptions);
        if (json.Length > maxJsonBytes)
        {
            return false;
        }

        var tally = Tallies.GetValue(state, static _ => new PersistedTally());
        lock (tally)
        {
            if (!tally.Fits(key, json.Length, MaxTotalJsonBytes))
            {
                return false;
            }

            // PersistentComponentState takes no raw bytes publicly, so the measured JSON goes in as a
            // JsonElement: PersistAsJson writes it out token for token, without serializing TValue again.
            // Counted only once PersistAsJson has accepted it, so a throw leaves the tally untouched.
            using var document = JsonDocument.Parse(json);
            state.PersistAsJson(key, document.RootElement);
            tally.Add(key, json.Length);
            return true;
        }
    }

    /// <summary>The keys and JSON bytes one state instance has taken so far.</summary>
    private sealed class PersistedTally
    {
        private readonly HashSet<string> _keys = [with(StringComparer.Ordinal)];
        private int _totalBytes;

        /// <summary>
        /// Whether <paramref name="bytes"/> more fit under <paramref name="budget"/>. A key already
        /// counted means a new persistence pass, so the tally restarts from zero first.
        /// </summary>
        public bool Fits(string key, int bytes, int budget)
        {
            if (_keys.Contains(key))
            {
                _keys.Clear();
                _totalBytes = 0;
            }

            return _totalBytes + bytes <= budget;
        }

        public void Add(string key, int bytes)
        {
            _keys.Add(key);
            _totalBytes += bytes;
        }
    }
}
