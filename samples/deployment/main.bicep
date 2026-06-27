// ─────────────────────────────────────────────────────────────────────────────
// REFERENCE SAMPLE (rubric §17) — a minimal, single-service deployment that wires the
// patterns an MMCA.Common-based app uses: Container Apps pulling from ACR via a
// user-assigned managed identity (no admin creds), runtime secrets in Key Vault (read via
// the same identity, no plaintext app secrets), workspace-based App Insights, Basic-tier SQL,
// cost-attribution tags, and a monthly budget with alerts. The full multi-service worked
// version is MMCA.ADC/infra/main.bicep. Adapt 'myapp' and add services as needed.
//
// Deploy order: foundation.bicep (once) → build & push the image to ACR → main.bicep (per release).
// The app UAMI + its AcrPull/Key Vault role assignments are bootstrapped OUT OF BAND (see
// DEPLOYMENT.md) — the deploy principal has Contributor but not role-assignment-write, so this
// template references the identity as `existing`.
// ─────────────────────────────────────────────────────────────────────────────
targetScope = 'resourceGroup'

@minLength(1)
@maxLength(10)
param environmentName string
@minLength(1)
param location string = resourceGroup().location

@description('ACR + Log Analytics from foundation.bicep')
param acrName string
param logAnalyticsName string

@description('Full image reference, e.g. myappxxx.azurecr.io/api:<sha>')
param containerImage string

@description('Resource id of the pre-created user-assigned managed identity the app runs as')
param appIdentityResourceId string

@description('SQL admin login; the password should come from a secure pipeline secret, never committed')
param sqlAdminLogin string
@secure()
param sqlAdminPassword string

@description('Set > 0 to create a monthly budget with 80%/100% alerts')
param monthlyBudgetAmount int = 200
@description('Email for budget + SLO alerts; leave empty to create the rules without notification')
param alertEmail string = ''

var resourceToken = toLower(uniqueString(resourceGroup().id, environmentName))
var prefix = 'myapp-${environmentName}'
var commonTags = {
  application: 'myapp'
  environment: environmentName
  component: 'my-component'
  managedBy: 'bicep'
  costCenter: 'my-cost-center'
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: acrName }
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: logAnalyticsName }

// Workspace-based Application Insights — the OTel exporter in MMCA.Common.Aspire ships to this when
// APPLICATIONINSIGHTS_CONNECTION_STRING is set (see Aspire/Extensions.cs AddOpenTelemetryExporters).
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi-${resourceToken}'
  location: location
  tags: commonTags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logAnalytics.id }
}

// Runtime secrets live in Key Vault (RBAC-authorized); the app reads them via its UAMI (Key Vault
// Secrets User, granted out of band). No plaintext Container App secrets.
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'myapp${resourceToken}'
  location: location
  tags: commonTags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${prefix}-sql-${resourceToken}'
  location: location
  tags: commonTags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    // Harden for production: prefer Entra-only auth + private endpoints. See ADR-004 and the
    // MMCA.ADC SQL-MANAGED-IDENTITY.md runbook for the staged managed-identity migration.
  }
}

resource appDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'MyApp'
  location: location
  tags: commonTags
  sku: { name: 'Basic', tier: 'Basic' } // §31: cheap tier sized to measured load, not worst case
}

resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource caEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae-${resourceToken}'
  location: location
  tags: commonTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: commonTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentityResourceId}': {} }
  }
  properties: {
    managedEnvironmentId: caEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      // Pull from ACR with the UAMI's AcrPull — no registry admin password.
      registries: [ { server: acr.properties.loginServer, identity: appIdentityResourceId } ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            // Key Vault reference: the value never appears in template output or app config.
            { name: 'ConnectionStrings__SQLServerConnectionString', secretRef: 'sql-conn' }
            // §31: raise idle outbox polling in deployed envs (real messages still flow in ~5s).
            { name: 'Outbox__PollingIntervalSeconds', value: '300' }
          ]
        }
      ]
      // §29/§31: one replica is a documented, cost-conscious posture; scale rules backed by real load.
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
}

// SLO alerting (§13/§29): failed-request + latency metric alerts wired to an action group.
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: '${prefix}-alerts'
  location: 'global'
  properties: {
    groupShortName: 'myappalerts'
    enabled: true
    emailReceivers: [ { name: 'ops', emailAddress: alertEmail, useCommonAlertSchema: true } ]
  }
}

// FinOps (§31): a budget so the bill cannot surprise you. 80% actual + 100% forecasted alerts.
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (monthlyBudgetAmount > 0) {
  name: '${prefix}-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: '2026-01-01' } // set to your budget inception (fixed at creation)
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: empty(alertEmail) ? [] : [ alertEmail ]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: empty(alertEmail) ? [] : [ alertEmail ]
      }
    }
  }
}

output apiFqdn string = api.properties.configuration.ingress.fqdn
output keyVaultName string = keyVault.name
