using System.Globalization;
using System.Text;

namespace MMCA.Common.Shared.Calendars;

/// <summary>
/// Dependency-free RFC 5545 iCalendar writer for "add to calendar" exports. Deliberately
/// minimal: UTC-only timestamps (no VTIMEZONE blocks), TEXT escaping, CRLF line endings, and
/// 75-octet line folding — the subset every calendar app imports reliably. Deterministic by
/// design: the caller supplies <c>dtStamp</c>, so identical inputs produce identical output.
/// </summary>
public static class IcsCalendarBuilder
{
    private const int MaxLineOctets = 75;

    /// <summary>
    /// Builds a complete <c>VCALENDAR</c> document.
    /// </summary>
    /// <param name="productId">RFC 5545 PRODID (e.g. <c>-//MMCA//AtlDevCon//EN</c>).</param>
    /// <param name="events">The entries to include, in output order.</param>
    /// <param name="dtStamp">The DTSTAMP instant recorded on every entry (pass the current time).</param>
    public static string Build(string productId, IReadOnlyCollection<IcsEvent> events, DateTimeOffset dtStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        ArgumentNullException.ThrowIfNull(events);

        var builder = new StringBuilder();
        AppendLine(builder, "BEGIN:VCALENDAR");
        AppendLine(builder, "VERSION:2.0");
        AppendLine(builder, "PRODID:" + EscapeText(productId));
        AppendLine(builder, "CALSCALE:GREGORIAN");
        AppendLine(builder, "METHOD:PUBLISH");

        foreach (var icsEvent in events)
        {
            AppendEvent(builder, icsEvent, dtStamp);
        }

        AppendLine(builder, "END:VCALENDAR");
        return builder.ToString();
    }

    private static void AppendEvent(StringBuilder builder, IcsEvent icsEvent, DateTimeOffset dtStamp)
    {
        AppendLine(builder, "BEGIN:VEVENT");
        AppendLine(builder, "UID:" + EscapeText(icsEvent.Uid));
        AppendLine(builder, "DTSTAMP:" + FormatUtc(dtStamp));
        AppendLine(builder, "DTSTART:" + FormatUtc(icsEvent.StartsAtUtc));
        AppendLine(builder, "DTEND:" + FormatUtc(icsEvent.EndsAtUtc));
        AppendLine(builder, "SUMMARY:" + EscapeText(icsEvent.Summary));

        if (!string.IsNullOrWhiteSpace(icsEvent.Description))
        {
            AppendLine(builder, "DESCRIPTION:" + EscapeText(icsEvent.Description));
        }

        if (!string.IsNullOrWhiteSpace(icsEvent.Location))
        {
            AppendLine(builder, "LOCATION:" + EscapeText(icsEvent.Location));
        }

        AppendLine(builder, "END:VEVENT");
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC 5545 §3.3.11 TEXT escaping: backslash, semicolon, comma, and newlines.</summary>
    private static string EscapeText(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// RFC 5545 §3.1 content-line writer: CRLF terminators and folding at 75 octets (UTF-8),
    /// continuation lines prefixed with one space. Folds never split a multi-byte character:
    /// octets are counted per char (surrogate pairs as a unit) before appending.
    /// </summary>
    private static void AppendLine(StringBuilder builder, string line)
    {
        var octets = 0;
        var i = 0;
        while (i < line.Length)
        {
            var charCount = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
            var charOctets = Encoding.UTF8.GetByteCount(line, i, charCount);

            if (octets + charOctets > MaxLineOctets)
            {
                builder.Append("\r\n ");
                octets = 1; // the folding space counts against the continuation line's budget
            }

            builder.Append(line, i, charCount);
            octets += charOctets;
            i += charCount;
        }

        builder.Append("\r\n");
    }
}
