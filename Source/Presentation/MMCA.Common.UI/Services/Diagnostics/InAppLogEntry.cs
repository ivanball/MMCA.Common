using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>One log record held by <see cref="InAppLogBuffer"/>.</summary>
/// <param name="Timestamp">When the record was written (UTC).</param>
/// <param name="Level">The record's level.</param>
/// <param name="Category">The logger category, normally the full name of the logging type.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">
/// The exception's type and message (inner exceptions appended), or <see langword="null"/> when the
/// record carries none. The stack trace is not kept.
/// </param>
public sealed record InAppLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    /// <summary>
    /// Formats the entry as one line (two when it carries an exception): the UTC timestamp, the
    /// level, the category and the message. This is the text that is copied and shared.
    /// </summary>
    /// <returns>The entry as text.</returns>
    public override string ToString()
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}Z [{Level}] {Category}: {Message}");
        return Exception is null ? line : line + Environment.NewLine + "    " + Exception;
    }
}
