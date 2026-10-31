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
/// to restore fetches on its interactive pass, exactly as it does when it never prerendered.
/// </remarks>
internal static class PrerenderStateBudget
{
    /// <summary>
    /// The largest UTF-8 JSON payload one key may persist: 14 KB, under half of SignalR's 32 KB default.
    /// The headroom is deliberate. Every component on the page persists into the same circuit-start
    /// message, and the state travels re-encoded: each value is base64 inside the state dictionary, and
    /// the dictionary is Data Protection protected and base64 encoded again, so each JSON byte costs about
    /// 16/9 of a byte on the wire. 14 KB of JSON therefore lands near 25 KB, leaving room for the other
    /// components' state, the protector's envelope and the hub framing.
    /// </summary>
    internal const int MaxJsonBytes = 14 * 1024;

    // The options PersistAsJson and TryTakeFromJson use, so bytes persisted here restore through
    // TryTakeFromJson unchanged.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes <paramref name="instance"/> once and persists that JSON under <paramref name="key"/>
    /// when it fits <paramref name="maxJsonBytes"/>; otherwise persists nothing.
    /// </summary>
    /// <typeparam name="TValue">The persisted type; restore it with <c>TryTakeFromJson&lt;TValue&gt;</c>.</typeparam>
    /// <param name="state">The component's persistent state.</param>
    /// <param name="key">The persistence key.</param>
    /// <param name="instance">The value to persist.</param>
    /// <param name="maxJsonBytes">The budget in UTF-8 JSON bytes; defaults to <see cref="MaxJsonBytes"/>.</param>
    /// <returns><see langword="true"/> when the value was persisted; <see langword="false"/> when it was over budget.</returns>
    internal static bool TryPersistAsJsonWithinBudget<TValue>(
        this PersistentComponentState state,
        string key,
        TValue instance,
        int maxJsonBytes = MaxJsonBytes)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(instance, SerializerOptions);
        if (json.Length > maxJsonBytes)
        {
            return false;
        }

        // PersistentComponentState takes no raw bytes publicly, so the measured JSON goes in as a
        // JsonElement: PersistAsJson writes it out token for token, without serializing TValue again.
        using var document = JsonDocument.Parse(json);
        state.PersistAsJson(key, document.RootElement);
        return true;
    }
}
