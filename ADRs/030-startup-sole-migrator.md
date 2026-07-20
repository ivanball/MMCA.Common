# ADR-030: Each Service Self-Applies Its Migrations at Startup (Sole Migrator)

## Status
Accepted (2026-06-27).

## Context
Under database-per-service (ADR-006), each service owns its own database and its own migrations project,
so *something* must apply pending migrations on every deploy. The framework's
`DatabaseInitializationExtensions` offers three strategies via `ApplicationSettings.DatabaseInitStrategy`,
acting per physical SQL Server source:

- `"Migrate"` — auto-apply pending EF Core migrations (the code documents this as **development/testing**).
- `"EnsureCreated"` — legacy `EnsureCreated` for every source in use.
- `"None"` — the **production** guard: validate that no SQL Server source has unapplied migrations and
  throw a per-source breakdown if any is behind.

The framework's own comments mark `"None"` as the production strategy and `"Migrate"` as dev/test. Both
production apps deliberately diverge from that default, and the divergence was bought with an incident, so
it deserves to be recorded.

## Decision
In Azure Container Apps, **every service host runs `ApplicationSettings__DatabaseInitStrategy = Migrate`
in production and is the sole migrator of its own database** — it applies its pending EF Core migrations
at startup, before the new revision serves traffic. There is deliberately **no** separate deploy-step
migration (no `sqlcmd` / `dotnet ef database update` apply in `deploy.yml`).

- **Set in prod for every service.** `MMCA.Store/infra/main.bicep:704,804,892` (Identity/Catalog/Sales)
  and `MMCA.ADC/infra/main.bicep:831,972,1067,1184` (Identity/Conference/Engagement/Notification) all set
  `DatabaseInitStrategy = 'Migrate'`.
- **One applier per revision.** Each service runs `minReplicas: 1`, so the startup `MigrateAsync` is not
  racing sibling replicas of the same revision. (Since the 2026-07-19 outbox lease revision, ADR-003,
  this migration serialization is the only correctness reason left for `minReplicas: 1`; the outbox
  is scale-out safe by construction, so above one replica the setting is a cost/migration choice.)
- **No deploy-step backstop, on purpose.** Both `deploy.yml` files carry an explicit comment that there
  is *no external `sqlcmd` migration backstop* and that each service is the **sole migrator**
  (`MMCA.Store/.github/workflows/deploy.yml:642`, `MMCA.ADC/.github/workflows/deploy.yml:658`). The
  `sqlcmd` that *is* installed in the pipeline is a connectivity/readiness probe, not a migration apply.
- **Build-time drift gate, not a runtime apply.** CI runs
  `dotnet ef migrations has-pending-model-changes` (Store `deploy.yml:102`, ADC `deploy.yml:99`) so a
  model that has drifted from its migrations fails the build — but that gate only *detects*; it never
  applies anything. The container does the applying.
- **This overrides the framework's documented "None for production" default**, accepting auto-migrate-on-
  boot in prod as the price of one fewer moving part.
- **It came from a real incident.** A previous `sqlcmd` migration backstop in `deploy.yml` *raced* the
  container's own startup `Migrate()` on a fresh per-service database, creating a table without its
  `__EFMigrationsHistory` row and wedging Store's first per-service deploy. The fix (recorded inline in
  both `deploy.yml`) was to delete the backstop and let the service be the sole migrator.

## Rationale
- **One migrator, one mechanism.** The code that owns the schema applies the schema; there is no second
  tool to keep in lockstep and no ordering race between a deploy step and container boot — exactly the
  failure the incident exposed.
- **Database-per-service keeps each migration small and scoped.** A boot-time apply touches only one
  service's database, so it is fast and its blast radius is one service.
- **Idempotent re-runs.** EF Core's `__EFMigrationsHistory` table means an already-applied migration is
  skipped, so a restart or redeploy that re-enters startup migration is a no-op.

## Trade-offs
- **Auto-migrate-in-production is what `"None"` exists to prevent.** An unintended or destructive
  migration would ship itself on the next deploy. The apps accept this; the build-time model-drift gate
  is the compensating control, and the per-service blast radius bounds the damage.
- **A failed startup migration fails the new revision.** ACA keeps traffic on the previous revision
  (readiness gating, ADR-025), but a *half-applied* migration still needs manual recovery — there is no
  automated down-migration.
- **Rolling updates briefly overlap two revisions.** `minReplicas: 1` keeps it to one applier per
  revision, but during a rollout the old and new revisions coexist for a window; a long migration can
  delay the new revision's readiness.
- **Recovery is per database.** Backups/restore are per service (ADR-006 / ADR-009), so rolling back a
  bad migration is a per-database operation, not an app-wide one.

## Related
ADR-006 (database-per-service: why each service owns and migrates its own database),
ADR-025 (readiness gating keeps traffic off a still-migrating replica),
ADR-009 (RTO/RPO + drilled restore is the recovery backstop for a bad migration).
