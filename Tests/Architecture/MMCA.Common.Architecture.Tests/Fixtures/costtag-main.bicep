// Fixture for CostTagConventionTestsBaseTests: two tagged container apps and the per-service
// database tag, in the shape the consumers' infra/main.bicep uses.
var commonTags = {
  application: 'fixture'
}

resource sqlDatabases 'Microsoft.Sql/servers/databases@2023-08-01-preview' = [for dbName in databaseNames: {
  parent: sqlServer
  name: dbName
  location: location
  tags: union(commonTags, { service: toLower(replace(dbName, 'Fixture_', '')) })
}]

resource catalogApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-fixture-catalog'
  location: location
  tags: union(commonTags, { service: 'catalog' })
}

resource gatewayApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-fixture-gateway'
  tags: union(commonTags, { service: 'gateway' })
}
