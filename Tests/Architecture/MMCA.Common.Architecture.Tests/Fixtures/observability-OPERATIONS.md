# Fixture operations runbook

Companion to `observability-main.bicep`. Every `### ...-alert-<key> (sev N)` heading below pairs with
a spec in that template, and the severity in each heading matches the spec, so the base class's
pairing gate passes against this fixture pair.

## Alert triage

### fixture-alert-failed-requests (sev 2)

Triage steps for the failed-requests alert.

### fixture-alert-response-time (sev 3)

Triage steps for the response-time alert.

### fixture-alert-dependency-failures (sev 2)

Triage steps for the dependency-failures alert.
