// Fixture for ObservabilityConventionTestsBase. Deliberately minimal: it carries only the two parse
// anchors the base looks for (the sloAlertSpecs declaration and the sloAlerts resource that
// materializes it) plus three well-formed specs, so the base's discovery and pairing logic can be
// exercised without embedding a real consumer's several-thousand-line template.

var sloAlertSpecs = [
  {
    key: 'failed-requests'
    severity: 2
  }
  {
    key: 'response-time'
    severity: 3
  }
  {
    key: 'dependency-failures'
    severity: 2
  }
]

resource sloAlerts 'Microsoft.Insights/metricAlerts@2018-03-01' = [
  for spec in sloAlertSpecs: {
    name: 'fixture-alert-${spec.key}'
  }
]
