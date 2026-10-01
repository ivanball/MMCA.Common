# Privacy and Data Protection

MMCA.Common is a framework, not a deployed service: it stores no personal data of its own. It ships the building blocks a consuming application uses to meet GDPR/CCPA obligations, and it leaves the decisions that depend on the application's data and deployment to that application. ADR-005 is the design record.

## What the framework provides

- **The `[Pii]` marker.** `Source/Core/MMCA.Common.Domain/Attributes/PiiAttribute.cs` marks a property as a data subject's personal data. The framework marks its own personal-data members, such as `RefreshSession.IpAddress` and `RefreshSession.UserAgent` (`Source/Core/MMCA.Common.Domain/Auth/RefreshSession.cs`).
- **Erasure in place (`IAnonymizable`).** `Source/Core/MMCA.Common.Domain/Interfaces/IAnonymizable.cs`: an entity with `[Pii]` members overwrites them with non-identifying values in an idempotent `Anonymize()`, keeping the row so foreign keys and the audit trail survive. Soft delete alone does not erase.
- **Redaction (`PiiRedactor`).** `Source/Core/MMCA.Common.Domain/Privacy/PiiRedactor.cs` masks every `[Pii]` member before an entity reaches a structured log or a telemetry attribute.
- **The erasure extension point.** `IErasableUser` (`Source/Core/MMCA.Common.Domain/Auth/IErasableUser.cs`) plus `DeleteUserHandlerBase` (`Source/Core/MMCA.Common.Application/Users/UseCases/DeleteUser/`) soft-delete the account, anonymize it, and revoke its issued tokens; the app adds its own tail.
- **The fitness-gated contract.** `PiiConventionTestsBase` (`Source/Hosting/MMCA.Common.Testing.Architecture/Bases/Governance/`) fails the build when a Domain type declares `[Pii]` without implementing `IAnonymizable`. In this repo, `Tests/Architecture/MMCA.Common.Architecture.Tests/Governance/PiiConventionTests.cs` also fails on a personal-data member (email, name, phone, address) that lacks `[Pii]`, and `PiiErasureContractFitnessTests.cs` proves marker, redaction and erasure compose end to end.
- **Data-subject access export (DSAR).** `ExportUserDataHandlerBase` and `IUserDataExportSection` (`Source/Core/MMCA.Common.Application/Users/UseCases/ExportUserData/`) assemble one package from every module's section, served by `DataExportControllerBase` (`Source/Presentation/MMCA.Common.API/Controllers/Privacy/`) as `GET {userId}/export` on the consumer's users route.
- **Retention purges.** `AuditTrailCleanupJob` (`Source/Core/MMCA.Common.Infrastructure/Persistence/AuditTrail/`) deletes audit rows older than `AuditTrail:RetentionDays` (default 90) when the host runs the scheduler. `RefreshSessionCleanupService` (`Source/Core/MMCA.Common.Infrastructure/Persistence/Auth/`) hard-deletes spent sessions, with their IP and user-agent, after `RefreshSessions:RetentionDays`.
- **Fail-closed CSV export.** `EntityControllerBase.ExportAsync` (`Source/Presentation/MMCA.Common.API/Controllers/EntityControllerBase.cs`) streams only the rows `GetReadSpecificationAsync` (or `GetExportSpecification`) allows. When no row scope resolves, the export is refused with a 403 (`Export.RowScopeRequired`) unless the controller opts in to a whole-table export by overriding `AllowUnscopedExport`.
- **Field encryption.** `EncryptedStringConverter` (AES-256-GCM) for personal fields that must stay retrievable; see `SECURITY.md`.

## What the consuming application owns

- **The personal-data inventory.** Which of its entities and fields hold personal data, marked with `[Pii]`, each with an `Anonymize()` that erases them. Subclass `PiiConventionTestsBase` so the build enforces it.
- **Lawful basis and consent capture.** The framework records no consent and assumes no lawful basis; the application decides, captures and stores both.
- **Data residency.** Where its databases actually live, stated in its own `PRIVACY.md` and verified by subclassing `DataResidencyTestsBase` (`Source/Hosting/MMCA.Common.Testing.Architecture/Bases/Governance/DataResidencyTestsBase.cs`) against its deployment source of truth.
- **Retention periods.** The values of `AuditTrail:RetentionDays`, the session retention, and every retention rule for its own tables, plus running the scheduler that applies them.
- **Row scoping.** A row scope on every controller that exports rows a caller should not see in full, and a deliberate `AllowUnscopedExport` only where the whole table is meant for every caller who can reach the endpoint.
- **DSAR coverage.** One `IUserDataExportSection` per module that stores a data subject's data, so the access export is complete.
