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
@description('Tokens per 15 min across the MMCA.Common.AI meter that should page (0 disables nothing: the rule is always created). Size it from your own baseline; 200k is a placeholder.')
param aiTokenAlertThreshold int = 200000

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

// The runtime connection string is composed here and stored ONLY in Key Vault. The Container App
// below binds it by reference (keyVaultUrl + the app UAMI), so the value never lands in the app's
// configuration as plaintext. Writing the secret needs the DEPLOY principal to hold Key Vault
// Secrets Officer on the vault; reading it at runtime needs the APP UAMI to hold Key Vault Secrets
// User (both granted out of band, see DEPLOYMENT.md and the header note above).
var appSqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${appDb.name};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource kvSqlConn 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'sql-conn'
  properties: { value: appSqlConnectionString }
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
      // Runtime secrets are Key Vault references resolved by the platform at revision start using
      // the same UAMI (it needs Key Vault Secrets User on the vault, granted out of band). The
      // value is never stored as a plaintext Container App secret.
      secrets: [
        { name: 'sql-conn', keyVaultUrl: kvSqlConn.properties.secretUri, identity: appIdentityResourceId }
      ]
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

// SLO log-search alerts over the Log Analytics workspace (§13). Log-search rules rather than metric
// alerts because a metric alert cannot express a status-code or URL predicate, and at low traffic
// the un-predicated rules page on routine, non-incident requests: a 401 from an expired token and a
// 499 from a client that navigated away both count as failed requests. Every rule here has a
// matching triage section in OPERATIONS.md, and ObservabilityConventionTestsBase fails the build if
// one is added, renamed or re-tiered without the other moving with it.
//
// Thresholds are PLACEHOLDERS sized for a small single-service app. Set them from your own
// baseline: an alert that pages on normal traffic gets muted, and a muted alert is worse than none.
var sloAlertSpecs = [
  {
    key: 'failed-requests'
    description: 'Elevated failed HTTP requests over 15 min, excluding 401 (expired or absent auth) and 499 (client disconnected mid-request).'
    query: 'AppRequests | where Success == false | where ResultCode !in ("401", "499")'
    timeAggregation: 'Count'
    metricMeasureColumn: ''
    threshold: 10
    severity: 2
  }
  {
    key: 'server-response-time'
    description: 'Average server response time above 3s over 15 min. Exclude any long-lived connection endpoint (a SignalR hub reports its CONNECTION LIFETIME as request duration) before trusting this number.'
    query: 'AppRequests | summarize AggregatedValue = avg(DurationMs) | where isnotnull(AggregatedValue)'
    timeAggregation: 'Average'
    metricMeasureColumn: 'AggregatedValue'
    threshold: 3000
    severity: 3
  }
  {
    key: 'availability'
    description: 'More than 5% of HTTP requests failed over 15 min: the user-visible availability SLO, expressed as a rate so a traffic spike does not page on its own.'
    query: 'AppRequests | summarize AggregatedValue = 100.0 * countif(Success == false) / count() | where isnotnull(AggregatedValue)'
    timeAggregation: 'Average'
    metricMeasureColumn: 'AggregatedValue'
    threshold: 5
    severity: 1
  }
  {
    // The two counters MMCA.Common.AI publishes (see AiUsageMeter). Inert unless the app adds the
    // optional AI package and enables Ai:Enabled, so the rule is safe to provision either way.
    key: 'ai-token-spend'
    description: 'Language-model token spend over 15 min is above the configured ceiling: a runaway loop, a prompt regression, or genuine growth that the budget has not caught up with.'
    query: 'AppMetrics | where Name in ("mmca.ai.input_tokens", "mmca.ai.output_tokens") | summarize AggregatedValue = sum(Sum) | where isnotnull(AggregatedValue)'
    timeAggregation: 'Total'
    metricMeasureColumn: 'AggregatedValue'
    threshold: aiTokenAlertThreshold
    severity: 3
  }
]

resource sloAlerts 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = [
  for spec in sloAlertSpecs: {
    // The rule name is part of its identity in Azure: renaming one creates a SECOND rule beside the
    // live one rather than updating it. Keep these stable once deployed.
    name: '${prefix}-alert-${spec.key}'
    location: location
    tags: commonTags
    properties: {
      displayName: '${prefix}-alert-${spec.key}'
      description: spec.description
      severity: spec.severity
      enabled: true
      scopes: [ logAnalytics.id ]
      // Evaluated on the same cadence as the window it reads (FinOps, §31): a scheduled-query rule
      // is billed per evaluation, so a 5-min frequency over a 15-min window pays three times for
      // overlapping looks at the same data.
      evaluationFrequency: 'PT15M'
      windowSize: 'PT15M'
      autoMitigate: true
      criteria: {
        allOf: [
          // metricMeasureColumn is set only for the rules that evaluate an aggregate. Omitting it
          // (the empty-string case) makes the rule count returned ROWS, which is what a
          // failure-count rule wants; supplying it evaluates the named aggregated column instead.
          // union() is how Bicep expresses "add this property only sometimes".
          union(
            {
              query: spec.query
              timeAggregation: spec.timeAggregation
              operator: 'GreaterThan'
              threshold: spec.threshold
              failingPeriods: {
                numberOfEvaluationPeriods: 1
                minFailingPeriodsToAlert: 1
              }
            },
            empty(spec.metricMeasureColumn) ? {} : { metricMeasureColumn: spec.metricMeasureColumn }
          )
        ]
      }
      // With no alertEmail the rules are still created and still fire; they just notify nobody,
      // which is the useful half of the sample for an environment that has no on-call yet.
      actions: {
        actionGroups: empty(alertEmail) ? [] : [ actionGroup.id ]
      }
    }
  }
]

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
