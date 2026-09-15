# Operations Runbook

The triage half of the deployment sample. `main.bicep` provisions four SLO log-search alerts over
the Log Analytics workspace; each one has a section below saying what it means, what to look at
first, how to recover, and when to escalate. An alert nobody knows how to answer gets muted, and a
muted alert is worse than no alert, so the two files are kept in step by a build gate rather than by
discipline: `ObservabilityConventionTestsBase` (in `MMCA.Common.Testing.Architecture`) parses the
`sloAlertSpecs` array out of `main.bicep` and this file's headings, and fails the build when an alert
has no section, when a section names an alert that no longer exists, or when a heading's severity
disagrees with the template.

`<prefix>` below is the `prefix` variable in `main.bicep` (`myapp-<environmentName>`). Alert rule
names are part of their identity in Azure: renaming one creates a second rule beside the live one
instead of updating it.

Before triaging anything, check whether a deployment is in flight. A rolling revision swap moves
failure counts and response times on its own, and a single-sample window during a restart is not an
incident.

## Alert runbooks

### `<prefix>-alert-failed-requests` (sev 2): more than 10 failed HTTP requests in 15 minutes

**Symptom.** Requests are returning 4xx or 5xx above the routine floor. The rule already excludes 401
(expired or absent auth) and 499 (client disconnected mid-request), so what remains should be real.

**First checks.**
- `AppRequests | where Success == false | summarize count() by ResultCode, Name | order by count_ desc`
  over the alert window: one endpoint and one status code usually accounts for the whole burst.
- If the top row is a 404 on `/robots.txt` or `/sitemap.xml`, it is a crawler, not an incident; add
  the exclusion to the rule query rather than muting the rule.
- `AppExceptions` over the same window, joined on `OperationId`, for the stack behind a 500.
- Container App revision list: did a new revision go live inside the window?

**Recovery.**
- Bad release: `az containerapp revision copy -g $RG -n <app> --from-revision <last-good>`, which is
  the same rollback the post-deploy smoke gate performs.
- Dependency outage: confirm with `<prefix>-alert-availability` and the dependency telemetry, then
  follow that dependency's own runbook. The resilience pipeline is already failing fast, so the app
  stays up while the dependency does not.
- Bad input from one caller: nothing to do in the app; note the caller and move on.

**Escalate.** The burst continues past two evaluation windows (30 minutes) after a rollback, or the
failures touch authentication or payment paths.

### `<prefix>-alert-server-response-time` (sev 3): average server response time above 3s over 15 minutes

**Symptom.** The fleet-wide average request duration crossed the latency objective.

**First checks.**
- `AppRequests | summarize avg(DurationMs), count() by Name | order by avg_DurationMs desc`: an
  average is easy to poison, and one slow endpoint with a handful of calls can carry the whole
  number.
- Long-lived connections. A SignalR hub reports its CONNECTION LIFETIME as request duration, so a
  handful of open hub connections can push a fast fleet over the threshold. If the top rows are hub
  routes, exclude them from the rule query (`where Name !contains "/hubs/"`) rather than chasing
  latency that does not exist.
- `AppDependencies | summarize avg(DurationMs) by Type, Target`: a slow database or a slow outbound
  call shows up here first.
- Replica count and CPU: one replica under sustained load is a documented posture, not a bug, but it
  is also the first thing to change.

**Recovery.**
- Raise `maxReplicas` or add a scale rule backed by the real signal (concurrent requests), not by a
  guess.
- Slow query: check the plan and the index, not the app. The framework's read paths are
  `AsNoTracking` already.
- Cache: an `IQueryCacheable` query with a sensible `CacheDuration` removes repeat work without a
  schema change.

**Escalate.** The average stays above the threshold after scaling out, or p95 latency (not just the
average) has moved, which points at the dependency rather than at concurrency.

### `<prefix>-alert-availability` (sev 1): more than 5% of requests failed over 15 minutes

**Symptom.** The user-visible availability objective is breached. This is a RATE, so unlike the
failed-request count it does not page because traffic grew.

**First checks.**
- `/health` and `/alive` on the app: `/alive` says the process is up, `/health` says its dependencies
  answer. Alive but unhealthy points at the database, the cache or the broker.
- Revision status: a revision that failed to start leaves the old one serving, or nothing serving.
- `AppRequests | summarize Failed = countif(Success == false), Total = count() by bin(TimeGenerated, 1m)`
  to see whether this is a cliff (deployment, dependency loss) or a ramp (saturation).
- Key Vault and managed identity: an expired or revoked role assignment fails every request that
  needs a secret, and it fails them all at once.

**Recovery.**
- Roll back to the last good revision first and diagnose afterwards. Availability is the one alert
  where restoring service outranks understanding it.
- If the database is the cause, confirm the SQL server is reachable and within its DTU ceiling
  before restarting anything in the app.

**Escalate.** Immediately, in parallel with the first checks. This is the severity-1 rule: it does
not wait for a second opinion.

### `<prefix>-alert-ai-token-spend` (sev 3): language-model token spend above the ceiling over 15 minutes

**Symptom.** `mmca.ai.input_tokens` plus `mmca.ai.output_tokens` crossed `aiTokenAlertThreshold` in
one window. The rule is inert unless the app added the optional `MMCA.Common.AI` package and set
`Ai:Enabled`.

**First checks.**
- `AppMetrics | where Name startswith "mmca.ai." | summarize sum(Sum) by tostring(Properties["prompt_name"]), tostring(Properties["prompt_version"])`:
  the prompt dimensions exist for exactly this question. A single prompt name owning the spike is a
  regression in that prompt; spend spread evenly is growth.
- `mmca.ai.call.duration` by `outcome` over the same window: a burst of `error` outcomes beside the
  spend usually means a retry loop paying for the same answer repeatedly.
- Compare against the previous window. A ceiling sized from a stale baseline pages on normal traffic,
  which is a threshold bug, not an incident.

**Recovery.**
- Prompt regression: roll the prompt version back. `PromptContract` carries name, version and a hash
  of the system prompt, so the version that moved is identifiable rather than inferred.
- Runaway loop: the fastest kill switch is `Ai:Enabled=false`, which registers no `IChatClient` at
  all, so every feature that gates on `GetService<IChatClient>()` turns itself off.
- Tighten the bounds instead of turning the feature off: `Ai:MaxOutputTokens` and
  `Ai:PerCallInputTokenBudget` are enforced per call by `BoundedChatClient`.
- Genuine growth: raise `aiTokenAlertThreshold` and the monthly budget together, in the same change,
  so the two numbers keep telling the same story.

**Escalate.** Spend keeps climbing after the prompt rollback, or one window's spend is a large
fraction of the monthly budget.

## Copying this into a consumer

The sample is meant to be lifted. In your own repository:

1. Copy `main.bicep` and this file into `infra/`, then delete the alert sections you did not keep and
   write one for every alert you added. Keep the heading shape exactly: three hashes, the rule name
   containing `-alert-<key>`, and `(sev N)` matching the `severity` in `sloAlertSpecs`.
2. Embed both files in your architecture test project, with the logical names the base class expects:

```xml
<ItemGroup>
  <EmbeddedResource Include="..\..\..\infra\main.bicep">
    <LogicalName>infra.main.bicep</LogicalName>
  </EmbeddedResource>
  <EmbeddedResource Include="..\..\..\infra\OPERATIONS.md">
    <LogicalName>infra.OPERATIONS.md</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

3. Subclass the gate. The defaults already point at those two logical names, so the only thing worth
   overriding is the floor, which you raise as you provision more alerts:

```csharp
public sealed class ObservabilityConventionTests : ObservabilityConventionTestsBase
{
    protected override int MinimumAlertSpecs => 4;
}
```

The floor is what keeps the gate honest: discovering fewer specs than expected means the parse
anchors in `main.bicep` drifted, not that the alerts quietly disappeared.
