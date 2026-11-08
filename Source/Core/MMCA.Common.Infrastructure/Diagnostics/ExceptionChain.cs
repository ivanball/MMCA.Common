using System.Text;

namespace MMCA.Common.Infrastructure.Diagnostics;

/// <summary>
/// Renders an exception and its causes as one line for a background job's <c>LastError</c> column,
/// its log text and its span status.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the chain and not <see cref="Exception.Message"/>.</b> Wrapper exceptions hide the cause:
/// <c>SmtpException</c> says only "Failure sending mail." while the server's reply sits on
/// <see cref="Exception.InnerException"/>. Recording the outer message alone left 21 dead-lettered
/// attempts in Store production (2026-11-01) with nothing to diagnose.
/// </para>
/// <para>
/// <b>Shape.</b> The outer message, then <c>" ---> {Type}: {Message}"</c> for each cause, depth first,
/// looking inside an <see cref="AggregateException"/> at every inner exception. A level repeating the
/// previous message adds nothing and is skipped; the walk stops after <see cref="MaxLevels"/> levels.
/// No stack traces: the column and the span status are read by people, and the processors that
/// need a stack log the exception object itself. An exception with no cause renders as its bare
/// message, exactly as before. Callers still fit the result to their column width.
/// </para>
/// </remarks>
internal static class ExceptionChain
{
    /// <summary>The most levels (the outer exception included) one description renders.</summary>
    internal const int MaxLevels = 8;

    private const string Separator = " ---> ";

    /// <summary>Describes <paramref name="exception"/> and its causes on one line.</summary>
    /// <param name="exception">The exception a job or message failed with.</param>
    /// <returns>The outer message followed by each cause's type and message.</returns>
    internal static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var text = new StringBuilder(exception.Message);
        var previousMessage = exception.Message;
        var levels = 1;

        // Depth first, in order: an explicit stack of the causes still to visit, pushed in reverse so
        // the first inner exception of an aggregate is rendered first.
        var pending = new Stack<Exception>();
        PushCauses(pending, exception);

        while (pending.Count > 0 && levels < MaxLevels)
        {
            var cause = pending.Pop();
            levels++;

            if (!string.Equals(cause.Message, previousMessage, StringComparison.Ordinal))
            {
                text.Append(Separator).Append(cause.GetType().Name).Append(": ").Append(cause.Message);
                previousMessage = cause.Message;
            }

            PushCauses(pending, cause);
        }

        return text.ToString();
    }

    private static void PushCauses(Stack<Exception> pending, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
            {
                pending.Push(aggregate.InnerExceptions[index]);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            pending.Push(inner);
        }
    }
}
