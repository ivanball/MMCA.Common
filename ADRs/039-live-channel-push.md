# ADR-039: Live channel push (ephemeral events over the notification hub)

## Status

Accepted (2026-07-09).

## Context

Conference-day features (live polls, session Q&A, live result counters) need sub-second fan-out of
small events to whoever is looking at a page right now. The existing notification pipeline
(ADR-024) is deliberately durable: one use case writes a per-user inbox row and fires a transient
SignalR push, with the inbox as the source of truth. Live updates are the opposite shape: high
frequency, broadcast to an interest group rather than a user list, worthless seconds later, and
wrong to persist. Routing them through the outbox/broker path adds seconds of latency
(`Outbox:ProcessingDelaySeconds`, ADR-003) and per-user rows nobody will read.

A second WebSocket/hub for live traffic would double the connection count per client, need its own
auth, reconnect, and backplane story, and split the client-side connection management in two.

## Decision

One realtime transport, two publisher seams:

- `NotificationHub` stays the single hub and gains its first client-invokable methods:
  `JoinChannel` / `LeaveChannel` map the calling connection into a SignalR group named by a channel
  key (`event:1`, `session:123`). Keys are validated against
  `PushNotificationSettings.ChannelKeyPattern` (default `^(event|session):[0-9]+$`); invalid keys
  throw `HubException`, so clients cannot join arbitrary group names.
- A new `ILiveChannelPublisher` abstraction in Application
  (`PublishAsync(channelKey, eventName, payloadJson)`) sits beside `IPushNotificationSender`.
  Payloads are pre-serialized JSON strings: one serialization point at the publishing edge, and the
  same payload shape rides any transport hop unchanged.
- Infrastructure ships `SignalRLiveChannelPublisher` (group send of `ReceiveChannelEvent` via
  `IHubContext<NotificationHub>`) and a no-op `NullLiveChannelPublisher` default, swapped in by
  `AddPushNotifications()` exactly like the ADR-024 sender pair. A host that does not map the hub
  can replace the registration with its own transport (for example a gRPC adapter that forwards to
  the service that does map the hub).
- `NotificationHubService` (UI) carries both traffic kinds on its existing connection:
  `JoinChannelAsync` / `LeaveChannelAsync` track membership, `OnChannelEvent` registers multicast
  subscriptions (an invisible layout listener and a page can observe the same channel
  concurrently), and every tracked channel is re-joined on `Reconnected`, because SignalR group
  membership does not survive an automatic reconnect.

The durable-vs-ephemeral split is decided at the publisher seam, not the transport:
`IPushNotificationSender` for anything a user must be able to find later (inbox-backed),
`ILiveChannelPublisher` for anything that only matters while it is on screen (no persistence, no
delivery guarantee).

## Rationale

- One WebSocket per client keeps connection management, token refresh, reconnect, and backplane
  behavior in one place; channel membership is a property of the existing connection.
- An Application-layer abstraction keeps application code transport-free (the same rule as
  `IMessageBus`, enforced by the architecture tests), and the Null default preserves the
  "resolvable everywhere, active only where configured" convention.
- String-JSON payloads avoid a serializer dependency in the contract and make cross-service
  ingress trivial (the payload is opaque to every hop).
- Group-name validation by configurable pattern closes the obvious abuse vector of client-supplied
  group names without hardcoding app-specific channel vocabularies into the framework.

## Trade-offs

- Ephemeral means lossy: a client that connects after an event was published never sees it.
  Features must treat channel events as cache-invalidation hints over fetchable state, not as the
  state itself.
- Handlers receive raw JSON and deserialize themselves; type safety is by convention (shared
  payload records in the consuming app's Shared project).
- The single settable `NotificationCallback` for durable notifications remains single-subscriber;
  only channel events are multicast. Unifying them is deliberate future work, not blocked by this
  decision.
- If a hub-hosting service ever runs more than one replica, the already-detected Redis backplane
  (ADR-024) is required for group sends to reach connections on other replicas; single-replica
  deployments need nothing.
