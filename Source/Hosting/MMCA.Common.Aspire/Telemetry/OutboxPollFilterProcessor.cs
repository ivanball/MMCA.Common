using System.Diagnostics;
using OpenTelemetry;

namespace MMCA.Common.Aspire.Telemetry;

/// <summary>
/// Suppresses outbox poll spans — and their children, such as the SqlClient dependency span
/// created by the Azure Monitor distro's automatic instrumentation — from telemetry export.
/// The <c>OutboxProcessor</c> polls every relational outbox table on a recurring cycle; in a
/// deployed environment those idle polls would otherwise dominate Application Insights /
/// Log Analytics ingestion (and spam the local Aspire dashboard). Real outbox work is
/// unaffected: per-message <c>OutboxProcess</c> spans use explicit parent contexts restored
/// from the stored trace ids and are never descendants of the poll span.
/// </summary>
public sealed class OutboxPollFilterProcessor : BaseProcessor<Activity>
{
    // Both names must stay in sync with OutboxProcessor in MMCA.Common.Infrastructure
    // (OutboxActivitySource / PollActivityName). Duplicated deliberately: the Aspire package
    // has no project references by design.
    private const string OutboxActivitySourceName = "MMCA.Common.Outbox";
    private const string PollActivityName = "OutboxPoll";

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (data is null)
        {
            // Never throw from a telemetry callback.
            return;
        }

        // Walk the in-process parent chain: matching on both source and operation name avoids
        // suppressing an unrelated consumer span that happens to be called "OutboxPoll".
        for (var current = data; current is not null; current = current.Parent)
        {
            if (current.OperationName == PollActivityName
                && current.Source.Name == OutboxActivitySourceName)
            {
                // Clearing Recorded makes the batch export processors (Azure Monitor, OTLP)
                // skip this activity. This processor is registered before the exporters, so
                // its OnEnd runs first.
                data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }
        }
    }
}
