namespace MMCA.Common.Architecture.Tests.ClockReadFixtures;

// Compiled-in fixtures for ClockReadTests: the clock-read rule reads IL, so its behaviour is pinned
// against real method bodies of each shape rather than against a reading of the rule.
/// <summary>Reads <c>DateTime.UtcNow</c> directly: the plainest offender.</summary>
internal sealed class UtcNowReadingFixture
{
    public DateTime Stamp() => DateTime.UtcNow;
}

/// <summary>Reads <c>DateTimeOffset.Now</c>: the local-time variant on the other clock type.</summary>
internal sealed class OffsetNowReadingFixture
{
    public DateTimeOffset Stamp() => DateTimeOffset.Now;
}

/// <summary>Reads the clock inside a lambda, whose body the compiler moves into a nested type.</summary>
internal sealed class LambdaClockReadingFixture
{
    public Func<DateTime> Clock() => () => DateTime.UtcNow;
}

/// <summary>Reads the clock after an await, whose body the compiler moves into a state machine.</summary>
internal sealed class AsyncClockReadingFixture
{
    public async Task<DateTimeOffset> StampAsync()
    {
        await Task.Yield();
        return DateTimeOffset.UtcNow;
    }
}

/// <summary>Takes time from an injected <see cref="TimeProvider"/>: the shape the rule asks for.</summary>
internal sealed class InjectedClockFixture(TimeProvider timeProvider)
{
    public DateTimeOffset Stamp() => timeProvider.GetUtcNow();
}

/// <summary>Two clock reads in one type, so a member-level allowlist entry can exempt exactly one.</summary>
internal sealed class TwoMemberClockFixture
{
    public DateTime Exempted() => DateTime.UtcNow;

    public DateTime StillReported() => DateTime.UtcNow;
}
