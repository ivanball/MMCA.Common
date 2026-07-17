# ADR-041: Observability and Telemetry Strategy

## Status
Accepted (2026-07-10).

## Context
The framework is a modular monolith whose modules extract into standalone services (ADR-008), so
the same telemetry has to make sense whether a request stays in one process or crosses a gateway and
several service hosts. OpenTelemetry auto-instrumentation (ASP.NET Core, `HttpClient`, the .NET
runtime) gives generic HTTP and runtime signals for free, but it is blind to the two paths that carry
almost all of the framework's own work: the CQRS use-case pipeline (ADR-014) and the outbox
(ADR-003). "How long is this command taking and how often does it fail" and "is the outbox
dead-lettering" are not questions auto-instrumentation can answer.

Two cost forces pull the other way. A deployed fleet polls every relational outbox around the clock,
so idle poll spans would dominate Application Insights ingestion if exported, and full-fidelity
tracing is the single largest observability line item. The framework needs custom instrumentation
where auto-instrumentation is blind, plus knobs that cut telemetry cost without going dark. This
cross-cutting observability decision was implemented but named by no existing ADR; this record
captures it.

## Decision
Standardize telemetry in the shared Aspire service defaults, add framework-specific instrumentation
for the CQRS and outbox paths, and expose cost knobs with fail-safe defaults.

- **One shared telemetry baseline on every host.** `ConfigureOpenTelemetry`
  (`Source/Hosting/MMCA.Common.Aspire/Extensions.cs:136`) wires OpenTelemetry logging with formatted
  messages and scopes (`Extensions.cs:139`), metrics from ASP.NET Core, `HttpClient`, and the runtime
  (`Extensions.cs:147`), and tracing from ASP.NET Core and `HttpClient` (`Extensions.cs:180`). It is
  called from `AddServiceDefaults` (`Extensions.cs:36`), so a host opts in once and every project in
  the Aspire model inherits the same pipeline.

- **Custom RED metrics from the CQRS pipeline.** A single meter `MMCA.Common.Cqrs`
  (`Source/Core/MMCA.Common.Application/UseCases/Decorators/CqrsMetrics.cs:15`) publishes two duration
  histograms: `cqrs.command.duration` (`CqrsMetrics.cs:20`) and `cqrs.query.duration`
  (`CqrsMetrics.cs:26`), both in milliseconds. The logging decorators record them in a `finally` so
  every path is measured: `CqrsMetrics.CommandDuration.Record(...)` tagged by `command` and `outcome`
  (`Source/Core/MMCA.Common.Application/UseCases/Decorators/LoggingCommandDecorator.cs:58`) and the
  query equivalent (`Source/Core/MMCA.Common.Application/UseCases/Decorators/LoggingQueryDecorator.cs:59`).
  The `outcome` tag takes `completed`, `failed` (a `Result` failure), or `exception`
  (`LoggingCommandDecorator.cs:30`, `:38`, `:52`), so count gives rate, the tag gives errors, and the
  histogram gives duration. The Aspire host subscribes the meter by literal name
  (`Extensions.cs:174`).

- **An outbox dead-letter counter.** The outbox processor owns a meter `MMCA.Common.Outbox`
  (`Source/Core/MMCA.Common.Infrastructure/Persistence/Outbox/OutboxProcessor.cs:59`) and a counter
  `outbox.dead_letter.count` (`OutboxProcessor.cs:60`), incremented with an `event_type` tag whenever
  a message is dead-lettered because its type cannot be resolved (`OutboxProcessor.cs:282`). The same
  source emits outbox spans (`OutboxProcessor.cs:58`); both the meter and the trace source are
  registered by literal name in the Aspire defaults (`Extensions.cs:173`, `Extensions.cs:179`).

- **Correlation-ID middleware ties the request together.** `CorrelationIdMiddleware`
  (`Source/Presentation/MMCA.Common.API/Middleware/CorrelationIdMiddleware.cs:15`) uses the
  `X-Correlation-ID` header (`CorrelationIdMiddleware.cs:18`), reading it from the request or falling
  back to the current W3C trace id and then to `HttpContext.TraceIdentifier`
  (`CorrelationIdMiddleware.cs:32`), sets it on the scoped `ICorrelationContext`
  (`CorrelationIdMiddleware.cs:36`), and echoes it on the response
  (`CorrelationIdMiddleware.cs:37`). The CQRS logging decorators stamp that same id into every log
  scope (`LoggingCommandDecorator.cs:23`, `:25`), so logs, the correlation id, and the trace id line
  up for one request.

- **Head-based sampling as a cost knob, off by default.** `Telemetry:TracesSampleRatio`
  (`Extensions.cs:195`, parsed by `TryGetTraceSampleRatio` at `Extensions.cs:210`) is unset by
  default, so a host samples everything and behavior does not change. A deployed host sets a ratio in
  the open interval (0,1) to keep that fraction of traces; the value wraps a `TraceIdRatioBasedSampler`
  in a `ParentBasedSampler` (`Extensions.cs:196`) so a sampled-in request keeps its whole trace across
  service boundaries. A key that is absent, unparseable, or outside (0,1) falls back to sample-all
  (`Extensions.cs:214`), so a typo can never silently drop all telemetry.

- **Outbox poll spans are filtered out of export.** `OutboxPollFilterProcessor`
  (`Source/Hosting/MMCA.Common.Aspire/Telemetry/OutboxPollFilterProcessor.cs:15`), registered before
  the exporters (`Extensions.cs:188`), clears the `Recorded` flag on the recurring `OutboxPoll` span
  and its children (`OutboxPollFilterProcessor.cs:42`). The poll query runs inside that span
  (`OutboxProcessor.cs:212`, named at `OutboxProcessor.cs:53`), so steady-state polling does not flood
  Application Insights. Real outbox work is untouched: each per-message `OutboxProcess` span is started
  under an explicit parent context restored from the message's stored trace and span ids
  (`OutboxProcessor.cs:327`), so it is never a child of the poll span.

- **Dual exporters, either or both.** `AddOpenTelemetryExporters` enables OTLP when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is present (`Extensions.cs:339`, the Aspire dashboard sets it) and
  Azure Monitor via `UseAzureMonitor` when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present
  (`Extensions.cs:347`, set by the cloud deployment). Both can be active at once
  (`Extensions.cs:334`), so local development ships to the Aspire dashboard and production ships to
  workspace-based Application Insights with no code change.

## Rationale
- **Instrument only where auto-instrumentation is blind.** The CQRS RED histograms and the outbox
  dead-letter counter cover the two framework-owned hot paths; everything else (HTTP, runtime) rides
  the free auto-instrumentation, so the custom surface stays small.
- **RED at the decorator, not in every handler.** The CQRS pipeline already wraps every handler in a
  logging decorator (ADR-014), so recording duration and outcome there makes metrics a byproduct of a
  seam that exists, with no per-handler discipline (the invariant-over-discipline posture, ADR-015).
- **A single correlation id with a W3C fallback.** Whether or not a client supplies
  `X-Correlation-ID`, one id stitches the logs of a request together and matches the trace, which is
  what an operator needs first when a distributed call goes wrong.
- **Cost knobs default to safe.** Sampling and poll-span filtering are the levers a FinOps owner
  reaches for (COST.md), and both fail toward keeping data: sampling is off unless configured, an
  out-of-range ratio is ignored, and only idle poll spans are dropped.
- **`ParentBased` keeps distributed traces coherent.** An extracted-service deployment (ADR-008) needs
  a sampled-in request to stay sampled end to end; a per-hop random sampler would shred cross-service
  traces.

## Trade-offs
- **Custom instrumentation carries a maintenance cost.** The Aspire package has no reference to
  Application or Infrastructure by design, so the meter and activity-source names are duplicated as
  literals (`Extensions.cs:171`, and the sync notes at `CqrsMetrics.cs:8` and
  `OutboxPollFilterProcessor.cs:17`). A rename on one side silently stops export until the literal is
  updated. That is the price of the decoupled package graph.
- **Sampling trades trace completeness for cost.** A sampled-out trace is simply gone; deep debugging
  of a specific request can miss it. Metrics and logs are unaffected (sampling is trace-only), so RED
  rates and error counts stay whole even at a low ratio.
- **Poll-span filtering hides steady-state outbox activity.** The dead-letter counter and per-message
  `OutboxProcess` spans remain, but "is the poller alive and looping" cannot be answered from traces
  alone, by design (that signal is metrics and the dead-letter counter, not spans).
- **Cross-service trace continuity depends on stored ids and the parent decision.** A linked
  `OutboxProcess` trace only reconnects when the producer captured the trace and span ids on the
  message; `ParentBased` sampling that dropped the originating trace also drops the linked span.
- **Exporters and sampling are opt-in per host.** A host that sets neither exporter variable emits to
  nothing, and a misconfigured ratio fails toward sample-all (higher cost) rather than toward silence:
  the intended bias, but it means a cost surprise is possible where a data gap is not.

## Related
ADR-003 (the outbox whose dead-letter counter and poll-span filtering this defines), ADR-014 (the
CQRS decorator pipeline that emits the RED histograms as a byproduct of its logging decorators),
ADR-009 (resilience and recovery objectives, configured alongside telemetry in the same
`AddServiceDefaults`; observability is the diagnostic layer under that posture), ADR-025 (startup
warm-up and readiness gating, whose health-check endpoints are the operational-signal sibling of these
telemetry signals in the same Aspire defaults), and COST.md (the FinOps companion that records
span-filtering and sampling as cost levers).
