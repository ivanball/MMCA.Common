namespace MMCA.Common.Architecture.Tests.DomainThrowFixtures;

/// <summary>
/// Compiled throw sites for <c>DomainThrowFitnessTests</c>. The rule reads IL, so the only honest way
/// to test it is to compile the throws it must (and must not) flag into this assembly and point a map
/// at it: the three argument guards, a business failure dressed as an exception, a custom domain
/// exception, a bare rethrow, a throw of a value built elsewhere, and a method that just returns.
/// </summary>
internal static class ArgumentGuardFixture
{
    internal static string Require(string? value) =>
        value ?? throw new ArgumentNullException(nameof(value));

    internal static void RequireInRange(int value)
    {
        if (value is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Must be between 1 and 99");
        }
    }

    internal static void RequireNotEmpty(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            throw new ArgumentException("Value must not be empty", nameof(value));
        }
    }
}

/// <summary>
/// Signals a business outcome by throwing. This is exactly what ADR-013 replaces with a
/// <c>Result.Failure</c>, so the rule must flag it.
/// </summary>
internal static class InvalidOperationThrowingFixture
{
    internal static void Close(bool alreadyClosed)
    {
        if (alreadyClosed)
        {
            throw new InvalidOperationException("The ticket is already closed");
        }
    }
}

/// <summary>A custom exception is the same defect wearing a domain name, so it must also be flagged.</summary>
public sealed class TicketDomainException : Exception
{
    public TicketDomainException()
    {
    }

    public TicketDomainException(string message)
        : base(message)
    {
    }

    public TicketDomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Throws the custom domain exception above.</summary>
internal static class CustomExceptionThrowingFixture
{
    internal static void Reject() => throw new TicketDomainException("The ticket was rejected");
}

/// <summary>
/// Preserves a caught exception with a bare rethrow, which compiles to the distinct <c>rethrow</c>
/// opcode. The rule must stay silent about it.
/// </summary>
internal static class RethrowingFixture
{
    internal static void Run(Action action, ICollection<string> log)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            log.Add("rethrown");
            throw;
        }
    }
}

/// <summary>
/// Throws a value it did not construct, so no static scan can name the exception type. The rule
/// reports it as UNVERIFIABLE instead of guessing.
/// </summary>
internal static class IndirectThrowFixture
{
    internal static void Fail(Exception prepared) => throw prepared;
}

/// <summary>Returns instead of throwing: the shape the rule exists to protect.</summary>
internal static class NonThrowingFixture
{
    internal static bool CanClose(bool alreadyClosed) => !alreadyClosed;
}
