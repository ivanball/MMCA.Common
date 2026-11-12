namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// Per-receiver cooldown state of one lane on this replica: a receiver whose real attempt failed cools
/// down for <c>cooldown</c>, and while it does the lane charges its due rows without sending them.
/// In memory only, used by one sequential lane, so it takes no lock.
/// </summary>
/// <param name="cooldown">How long a receiver cools down after a failed real attempt.</param>
internal sealed class ReceiverCooldowns(TimeSpan cooldown)
{
    private readonly Dictionary<string, DateTime> _until = [with(StringComparer.Ordinal)];

    /// <summary>Gets how many keys are tracked (test hook).</summary>
    internal int Count => _until.Count;

    /// <summary>Whether <paramref name="key"/> is cooling down: true while <paramref name="utcNow"/> is before its end.</summary>
    /// <param name="key">The receiver key.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <returns><see langword="true"/> while the key cools down.</returns>
    public bool IsCoolingDown(string key, DateTime utcNow) =>
        _until.TryGetValue(key, out var until) && utcNow < until;

    /// <summary>Cools <paramref name="key"/> down for one cooldown from <paramref name="utcNow"/>.</summary>
    /// <param name="key">The receiver key.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <returns><see langword="true"/> when the key went from not cooling to cooling.</returns>
    public bool Open(string key, DateTime utcNow)
    {
        var transition = !IsCoolingDown(key, utcNow);
        _until[key] = utcNow + cooldown;
        return transition;
    }

    /// <summary>Ends the cooldown of <paramref name="key"/>.</summary>
    /// <param name="key">The receiver key.</param>
    public void Close(string key) => _until.Remove(key);

    /// <summary>Drops every key whose cooldown ended more than one cooldown before <paramref name="utcNow"/>.</summary>
    /// <param name="utcNow">The current UTC instant.</param>
    public void Prune(DateTime utcNow)
    {
        foreach (var key in _until.Where(entry => entry.Value + cooldown < utcNow).Select(entry => entry.Key).ToList())
        {
            _until.Remove(key);
        }
    }
}
