# ADR-015: Architecture Invariants Enforced as Fitness Functions

## Status
Accepted

## Context
The codebase rests on invariants that are easy to state and easy to erode by accident: clean-
architecture layer flow (Domain depends on nothing above it), module isolation (no module reaches into
another's internals), transport staying at the edge (no MassTransit / gRPC / Protobuf in Domain /
Application / Shared, ADR-006/007/008), every integration event declaring a `SchemaVersion`
(ADR-010), every outbound client wiring resilience (ADR-009), and the MassTransit-v8 pin (ADR-016).
"Remember the rule" does not survive a growing change history. Several existing ADRs already rely on
"a fitness function enforces this" without an ADR that establishes the approach itself.

## Decision
Enforce architectural invariants as **automated checks that gate the build**, in two layers.

1. **Compile-time guard.** `MMCA.Common.LayerEnforcement.targets` (imported for every `Source/` project)
   inspects `ProjectReference`s in a pre-build step and **fails the build** before tests run if a layer
   references a forbidden upstream layer. This catches the most common mistake (a bad project reference)
   with the fastest possible feedback.
2. **Runtime fitness functions.** A shared `MMCA.Common.Testing.Architecture` package
   holds the rule bodies once: an `ArchitectureRules.*` library (layers, modules, transport, events,
   entities, handlers, naming, controllers, immutability, governance, purity, specifications, slices,
   and localization in two rules: resx translation-coverage and no-hardcoded-UI-literal text, among
   others) plus
   abstract `*TestsBase` classes parameterized by an `IArchitectureMap`. Each repo
   (Common / Store / ADC / Helpdesk) supplies a single `IArchitectureMap` implementation
   (`CommonArchitectureMap`, `StoreArchitectureMap`, `AdcArchitectureMap`, `HelpdeskArchitectureMap`)
   declaring its layer and module assemblies, then inherits the test bases. The same rules run
   identically everywhere via NetArchTest over the compiled assemblies.

These tests run inside the normal `dotnet test` / CI tier, so a violated invariant fails CI like any
other test (CI additionally guards with `--minimum-expected-tests 1` so an empty suite cannot pass
silently). Centralizing the rules in a package, rather than copying them per repo, means a new
invariant is written once and inherited by every consumer.

## Rationale
- **Invariant over discipline.** Turning "do not do X" into a red build is the only enforcement that
  scales. It is the same lever used by the layer rules, the resilience gate (ADR-009), the
  event-version gate (ADR-010), and the MassTransit pin (ADR-016).
- **Write once, run everywhere.** The `IArchitectureMap` seam keeps the four repos' rules in lockstep
  with zero duplication; a rule change lands for all consumers at once.
- **Two layers, two speeds.** The MSBuild guard fails at compile time on the common case; the
  NetArchTest suite catches the subtler assembly-level violations a project-reference check cannot see.

## Trade-offs
- The tests assert **structure / registration**, not runtime behavior. ADR-009's test proves a client
  *wires* resilience, not that its policy values are correct; parameter tuning stays a review concern.
- Some rules are necessarily reflection / convention based (naming, namespace shape) and can be
  brittle; keeping them in the shared library means a fix propagates everywhere in one change.
- A consumer must implement `IArchitectureMap` and subclass the bases to get the gating (opt-in wiring),
  even though the framework ships the rules. Common-only checks that cannot generalize live in
  `FrameworkSanityTests`.

## Related
ADR-009 (resilience gate), ADR-010 (event-version gate), ADR-016 (MassTransit pin gate), and
ADR-006/007/008 (the transport and module-isolation rules the suite enforces).
