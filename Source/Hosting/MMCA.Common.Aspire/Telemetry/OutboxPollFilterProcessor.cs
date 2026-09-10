using System.Diagnostics;
using OpenTelemetry;

namespace MMCA.Common.Aspire.Telemetry;

/// <summary>
/// Suppresses outbox poll spans — and their children, such as the SqlClient dependency span
/// created by the Azure Monitor distro's automatic instrumentation — from telemetry export.
/// The <c>OutboxProcessor</c> polls every relational outbox table on a recurring cycle, and the
/// <c>InternalCommandProcessor</c> polls every internal-command queue table the same way; in a
/// deployed environment those idle polls would otherwise dominate Application Insights /
/// Log Analytics ingestion (and spam the local Aspire dashboard). Real work is unaffected:
/// per-message <c>OutboxProcess</c> and per-command <c>InternalCommandExecute</c> spans use
/// explicit parent contexts restored from the stored trace ids and are never descendants of a
/// poll span.
/// </summary>
public sealed class OutboxPollFilterProcessor : BaseProcessor<Activity>
{
    // Every name here must stay in sync with MMCA.Common.Infrastructure: OutboxActivitySource /
    // PollActivityName on OutboxProcessor, and InternalCommandMetrics.MeterName plus
    // InternalCommandProcessor.PollActivityName on the queue side. Duplicated deliberately: this
    // package does not reference MMCA.Common.Infrastructure, so AddServiceDefaults stays usable
    // from a host that does not take the persistence stack (EF Core, MassTransit, SignalR/Redis).
    // Its one ProjectReference is MMCA.Common.Shared, for HttpResilienceDefaults. The same
    // literals also appear in the AddMeter / AddSource calls in Extensions.cs.
    private const string OutboxActivitySourceName = "MMCA.Common.Outbox";
    private const string PollActivityName = "OutboxPoll";
    private const string InternalCommandsActivitySourceName = "MMCA.Common.InternalCommands";
    private const string InternalCommandsPollActivityName = "InternalCommandPoll";

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (data is null)
        {
            // Never throw from a telemetry callback.
            return;
        }

        // Walk the in-process parent chain: matching on both source and operation name avoids
        // suppressing an unrelated consumer span that happens to share a poll's name.
        for (var current = data; current is not null; current = current.Parent)
        {
            if (IsSuppressedPoll(current))
            {
                // Clearing Recorded makes the batch export processors (Azure Monitor, OTLP)
                // skip this activity. This processor is registered before the exporters, so
                // its OnEnd runs first.
                data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }
        }
    }

    /// <summary>
    /// Whether one activity in the chain is a background poll this processor drops.
    /// </summary>
    /// <param name="activity">The activity to test.</param>
    /// <returns><see langword="true"/> when it is an outbox or internal-command poll span.</returns>
    private static bool IsSuppressedPoll(Activity activity) =>
        activity.OperationName == PollActivityName
            && activity.Source.Name == OutboxActivitySourceName
        || activity.OperationName == InternalCommandsPollActivityName
            && activity.Source.Name == InternalCommandsActivitySourceName;
}
