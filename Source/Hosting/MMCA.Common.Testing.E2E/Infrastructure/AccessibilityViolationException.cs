namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Thrown by <see cref="PageExtensions.AssertNoAccessibilityViolationsAsync"/> when an axe-core
/// accessibility scan finds one or more violations on the page under test.
/// </summary>
public sealed class AccessibilityViolationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AccessibilityViolationException"/> class.</summary>
    public AccessibilityViolationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AccessibilityViolationException"/> class with a message.</summary>
    public AccessibilityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AccessibilityViolationException"/> class with a message and inner exception.</summary>
    public AccessibilityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
