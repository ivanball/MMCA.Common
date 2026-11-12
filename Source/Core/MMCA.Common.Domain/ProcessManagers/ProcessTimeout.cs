namespace MMCA.Common.Domain.ProcessManagers;

/// <summary>
/// A named window measured from a process's last transition. The aggregate matches a fired timeout by
/// <see cref="Name"/> and computes its due instant with <see cref="DueAfter"/>.
/// </summary>
public sealed record ProcessTimeout
{
    /// <summary>Initializes a new instance of the <see cref="ProcessTimeout"/> class.</summary>
    /// <param name="name">The timeout's name; never blank.</param>
    /// <param name="window">How long after the last transition the timeout is due; strictly positive.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is zero or negative.</exception>
    public ProcessTimeout(string name, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        Name = name;
        Window = window;
    }

    /// <summary>Gets the timeout's name.</summary>
    public string Name { get; }

    /// <summary>Gets how long after the last transition the timeout is due.</summary>
    public TimeSpan Window { get; }

    /// <summary>Computes the instant this timeout is due for a process last transitioned at <paramref name="lastTransitionOn"/>.</summary>
    /// <param name="lastTransitionOn">The process's last transition instant.</param>
    /// <returns><paramref name="lastTransitionOn"/> plus <see cref="Window"/>.</returns>
    public DateTime DueAfter(DateTime lastTransitionOn) => lastTransitionOn + Window;
}
