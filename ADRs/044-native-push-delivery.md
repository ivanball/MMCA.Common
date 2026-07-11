# ADR-044: Native Push Delivery (the Third Notification Channel)

## Status
Accepted (2026-07-11). Amends ADR-024. The framework pipeline is implemented and inert by
default; each consumer switches it on by provisioning a notification hub with platform
credentials and enabling the `NativePush` configuration section.

## Context
ADR-024 established two notification channels: a durable per-user `UserNotification` inbox (the
source of truth) and a transient SignalR push behind `IPushNotificationSender`. Both stop at the
edge of a connected client: a phone with the app backgrounded, killed, or offline hears nothing
until the next launch. Conference announcements ("lunch is served", "room change") are exactly
the messages that must reach pockets, not open tabs.

OS-level delivery needs three things the framework did not have: a per-device registration
store keyed to users, a sender that speaks FCM v1 (Android) and APNs (iOS), and a client-side
lifecycle that registers the device after sign-in and unregisters it before sign-out. It also
needs credentials (a Firebase service account, an APNs auth key) that cannot live in source and
may not exist when the code ships.

## Decision
- **Azure Notification Hubs as the delivery fan-out.** One hub abstracts both platforms behind
  one API, holds the platform credentials outside our code, and its installation model gives
  upsert semantics with client-generated stable ids. The free tier covers conference volumes.
- **Two new Application abstractions, Null by default** (the ADR-024 pattern):
  `INativePushSender` (user-targeted and broadcast native sends) and `IPushDeviceRegistrar`
  (installation upsert/delete). `AddInfrastructure` TryAdds no-op defaults;
  `AddNativePushNotifications(configuration)` swaps in the Azure Notification Hubs
  implementations only when the `NativePush` section is enabled AND complete
  (`ConnectionString`, `HubName`) - so hosts call it unconditionally and deployments flip the
  channel on by configuration alone.
- **Installations are tagged `user:{id}`.** Sends target users, never raw tokens; a user's
  every device gets the message. Tag expressions are OR-chunked at the hub's 20-tag cap
  (`NativePushPayloads`, unit-tested), so audience size is unbounded.
- **`SendPushNotificationHandler` gains the third leg.** After the inbox write and the SignalR
  attempt, it calls `INativePushSender.SendToUsersAsync` inside its own non-fatal catch. The
  audit status (`Sent`/`Failed`) stays owned by the SignalR leg: the inbox remains the source
  of truth, and a hub outage must not fail the command or the other channels.
- **`DevicesController` (PUT/DELETE `/Notifications/Devices`)** ships in `MMCA.Common.API` via
  the existing `AddNotificationControllers` application part, `[Authorize]` for any signed-in
  user and feature-gated with the same `Notification.PushNotifications` flag as the rest of the
  pipeline. Ownership is stamped server-side from the current user; installation ids are
  client-generated GUIDs (not enumerable). DELETE is idempotent (unknown id = success).
- **Client orchestration behind two UI capability contracts** (ADR-042 pattern):
  `IPushRegistrationService` (register after sign-in, unregister BEFORE sign-out - the delete
  call is authenticated) and `IPushDeviceTokenProvider` (the platform token seam). UI.Maui
  ships `MauiPushRegistrationService` (stable installation id in `IDevicePreferences`, sync
  over the named API client); the token provider defaults to `NullPushDeviceTokenProvider`
  everywhere, so a build WITHOUT push credentials is wired but inert end to end.
  `PushRegistrationListener` (rendered through the host layout seam) re-registers on
  auth-state changes; `AuthUIService.LogoutAsync` owns the unregister leg.

## Consequences
- Sends fan out per 20-user chunk and per platform: an audience of N users costs
  `ceil(N/20) * 2` hub calls. Acceptable at conference scale; a template-based send can
  consolidate later without touching callers.
- The handler's third leg is fire-and-forget: no per-device delivery tracking. The hub's
  telemetry is the observability surface; the inbox remains the recovery path.
- Anyone holding an installation id could delete that registration through the authenticated
  DELETE. Ids are client-generated GUIDs stored only on the device, so this is not enumerable;
  re-registration on next launch self-heals.
- Consumers must update PRIVACY.md/store data-safety forms (push tokens are device identifiers)
  BEFORE store metadata mentions push. Credential provisioning (Firebase service account, APNs
  key) is a manual runbook step per app; until done, the hub rejects sends and the client
  token provider yields nothing - both by design.
- `SendPushNotificationHandler` gained a constructor parameter (DI-resolved; source-compatible
  for every host, breaking only for code constructing it manually - none known).
