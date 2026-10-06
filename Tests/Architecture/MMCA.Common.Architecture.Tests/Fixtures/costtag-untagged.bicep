// Fixture for CostTagConventionTestsBaseTests: one app carries only the shared tags, so its
// spend would fold into an unattributed bucket.
resource catalogApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-fixture-catalog'
  location: location
  tags: union(commonTags, { service: 'catalog' })
}

resource uiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-fixture-ui'
  location: location
  tags: commonTags
}
