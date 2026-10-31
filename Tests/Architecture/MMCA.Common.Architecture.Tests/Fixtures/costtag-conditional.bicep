// Fixture for CostTagConventionTestsBaseTests: a conditional app and a looped app, both with only
// the shared tags. Each must still be discovered and flagged, not skipped by the parse anchor.
resource catalogApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-fixture-catalog'
  location: location
  tags: union(commonTags, { service: 'catalog' })
}

resource optionalApp 'Microsoft.App/containerApps@2024-03-01' = if (deployOptional) {
  name: 'ca-fixture-optional'
  location: location
  tags: commonTags
}

resource workerApps 'Microsoft.App/containerApps@2024-03-01' = [for worker in workers: {
  name: 'ca-fixture-${worker}'
  location: location
  tags: commonTags
}]