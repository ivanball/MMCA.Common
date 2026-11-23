using System.Globalization;
using System.Text;

namespace MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

/// <summary>
/// Moves the framework's string-to-string headers in and out of AMQP header tables. Every value is
/// written as a string (an AMQP long string), and the client hands a long string back as UTF-8 bytes,
/// so the receive side decodes bytes back to the published string. Keys keep their case both ways.
/// </summary>
internal static class RabbitMqHeaders
{
    /// <summary>The prefix of the headers RabbitMQ itself stamps (<c>x-death</c>, <c>x-first-death-*</c>).</summary>
    private const string BrokerHeaderPrefix = "x-";

    /// <summary>Builds the AMQP header table for <paramref name="headers"/>, every value a string.</summary>
    /// <param name="headers">The framework headers.</param>
    /// <returns>The header table.</returns>
    internal static Dictionary<string, object?> Encode(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var table = new Dictionary<string, object?>(headers.Count, StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            table[key] = value;
        }

        return table;
    }

    /// <summary>
    /// Reads a delivered AMQP header table back into the framework's string-to-string headers. Byte
    /// arrays decode as UTF-8 and scalar values format invariantly. Headers RabbitMQ adds itself
    /// (<c>x-</c> prefixed, such as the <c>x-death</c> table a delay queue stamps) are not framework
    /// headers and are dropped, so a redelivered copy never accumulates them.
    /// </summary>
    /// <param name="table">The delivered header table, or <see langword="null"/>.</param>
    /// <returns>The framework headers, keyed ordinally.</returns>
    internal static Dictionary<string, string> Decode(IDictionary<string, object?>? table)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (table is null)
        {
            return headers;
        }

        foreach (var (key, value) in table)
        {
            if (key.StartsWith(BrokerHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string s => s,
                bool b => b ? "true" : "false",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => null,
            };

            if (text is not null)
            {
                headers[key] = text;
            }
        }

        return headers;
    }
}
