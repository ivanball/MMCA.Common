using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Application.UseCases.Crud;

/// <summary>
/// The per-command side channel of a mutate handler's load-mutate-save workflow: a typed bag for
/// values derived while the aggregate is loaded, plus the short-circuit signal that stops the write
/// without failing the command.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The workflow answers with the mutated aggregate, so anything a mutation
/// computed on the way (the aggregate's pre-mutation state, the blob name that is about to be
/// orphaned, a warning the caller has to be told about) had nowhere to go except handler instance
/// state, which a scoped handler must not carry between calls. One context instance is created per
/// command, handed to every hook in order (load, mutate, the post-save hooks) and finally to the
/// result builder, so the value travels with the command and dies with it.
/// </para>
/// <para>
/// <b>The short-circuit.</b> <see cref="SkipSave"/> marks the command as already satisfied: the
/// workflow returns the loaded aggregate successfully but issues no save and runs neither
/// <c>LogMutated</c> nor <c>OnMutatedAsync</c>. It is the idempotent no-op case (remove an avatar
/// that is not there, close an already-closed record), which is a success rather than a refused
/// invariant, and must not write a log line claiming a mutation happened.
/// </para>
/// <para>
/// Not thread-safe by design: one command is handled on one logical flow, and a hook that fans out
/// must collect its own results before writing them here.
/// </para>
/// </remarks>
public sealed class MutationContext
{
    private readonly Dictionary<string, object?> _items = [];

    /// <summary>
    /// Gets a value indicating whether the workflow was told to stop before the save. Set by
    /// <see cref="SkipSave"/>; the mutation still reports success.
    /// </summary>
    public bool SaveSkipped { get; private set; }

    /// <summary>Gets the side-data written so far, for a hook that wants to inspect the whole bag.</summary>
    public IReadOnlyDictionary<string, object?> Items => _items;

    /// <summary>
    /// Marks the command as already satisfied: no save, no <c>LogMutated</c>, no
    /// <c>OnMutatedAsync</c>, and the workflow still answers with the loaded aggregate as a success.
    /// Calling it more than once is harmless.
    /// </summary>
    public void SkipSave() => SaveSkipped = true;

    /// <summary>Writes a value into the bag, replacing any value already stored under the same key.</summary>
    /// <typeparam name="TValue">The value's type, recorded so a later read can verify it.</typeparam>
    /// <param name="key">The key to store under. Compared ordinally.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public void Set<TValue>(string key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        _items[key] = value;
    }

    /// <summary>Reads a value written earlier, verifying that it is of the requested type.</summary>
    /// <typeparam name="TValue">The type the value was written as.</typeparam>
    /// <param name="key">The key to read.</param>
    /// <param name="value">The value found, or the type's default when the key is absent or holds another type.</param>
    /// <returns><see langword="true"/> when the key held a <typeparamref name="TValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGet<TValue>(string key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_items.TryGetValue(key, out var stored) && stored is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads a value written earlier, answering the type's default when the key is absent or holds
    /// another type. The convenient read for a reference type or a value whose default is a
    /// meaningful answer; use <see cref="TryGet{TValue}(string, out TValue)"/> when "absent" and
    /// "default" have to be told apart.
    /// </summary>
    /// <typeparam name="TValue">The type the value was written as.</typeparam>
    /// <param name="key">The key to read.</param>
    /// <returns>The stored value, or the type's default.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public TValue? GetOrDefault<TValue>(string key) => TryGet<TValue>(key, out var value) ? value : default;

    /// <summary>Whether anything has been written under the key.</summary>
    /// <param name="key">The key to test.</param>
    /// <returns><see langword="true"/> when the key is present, whatever its value's type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _items.ContainsKey(key);
    }
}
