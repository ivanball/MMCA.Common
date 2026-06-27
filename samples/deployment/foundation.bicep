// ─────────────────────────────────────────────────────────────────────────────
// REFERENCE SAMPLE (rubric §17) — not deployed by MMCA.Common (a library can't deploy
// itself). It distills the IaC pattern a consumer app uses so deployment is reproducible,
// OIDC/managed-identity based, and cost-attributed. Worked, production version:
// MMCA.ADC/infra/foundation.bicep + main.bicep. Adapt 'myapp' to your app.
//
// foundation.bicep provisions the long-lived, rarely-changing resources (Log Analytics + ACR)
// that main.bicep then builds on. Deploy this once; main.bicep on every release.
// ─────────────────────────────────────────────────────────────────────────────
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(10)
@description('Environment name, e.g. prod')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string = resourceGroup().location

var resourceToken = toLower(uniqueString(resourceGroup().id, environmentName))
var prefix = 'myapp-${environmentName}'

// FinOps (§31): one consistent tag set on every billable resource so Azure Cost Analysis can group
// spend by application / environment / cost-centre. Mirror this exact set in main.bicep.
var commonTags = {
  application: 'myapp'
  environment: environmentName
  component: 'my-component'
  managedBy: 'bicep'
  costCenter: 'my-cost-center'
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs-${resourceToken}'
  location: location
  tags: commonTags
  properties: {
    sku: { name: 'PerGB2018' }
    // Retention is a real cost lever (§31): keep it to the minimum your compliance window allows.
    retentionInDays: 30
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'myapp${resourceToken}'
  location: location
  tags: commonTags
  sku: { name: 'Basic' }
  properties: {
    // Admin user disabled (§11/§17): apps pull via a managed identity (AcrPull) and the deploy
    // pushes via its own identity (AcrPush) — no long-lived admin credential exists anywhere.
    adminUserEnabled: false
  }
}

output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output logAnalyticsName string = logAnalytics.name
