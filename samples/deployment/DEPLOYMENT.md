# Reference Deployment (rubric §17)

MMCA.Common is a library and cannot deploy itself, so the §17 machinery (IaC, CD, OIDC/managed
identity) lives in the consumer apps. This folder is a **distilled reference** of that machinery so a
new app built on the framework can be deployed reproducibly, with no long-lived credentials and with
spend attributed from day one. The full, production-proven version is **MMCA.ADC/infra/** +
**MMCA.ADC/.github/workflows/deploy.yml**; this sample is the minimal single-service shape.

Files:
- [`foundation.bicep`](foundation.bicep) — long-lived resources (Log Analytics + ACR). Deploy once.
- [`main.bicep`](main.bicep) — per-release: Container App (pulls from ACR via managed identity),
  SQL (Entra-only authentication, no firewall rule until you add your own), Key Vault, App Insights,
  cost tags, budget + alerts.

## One-time bootstrap (out of band)

The deploy principal is intentionally given **Contributor but not role-assignment-write**, so the
managed identity and its role grants are created once, outside the templates (the templates reference
the identity as `existing`). This keeps the deploy identity least-privileged.

```bash
RG=myapp-rg
# 1) The user-assigned identity the app runs as
az identity create -n myapp-prod-apps-identity -g $RG -l eastus2
PRINCIPAL=$(az identity show -n myapp-prod-apps-identity -g $RG --query principalId -o tsv)

# 2) AcrPull so the app can pull images without the registry admin password
ACR_ID=$(az acr show -n <acrName> -g $RG --query id -o tsv)
az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope "$ACR_ID"

# 3) Key Vault Secrets User so the app reads runtime secrets via the same identity
KV_ID=$(az keyvault show -n <kvName> -g $RG --query id -o tsv)
az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$KV_ID"
# (az CLI 2.84 may misreport role writes — verify with `az role assignment list`.)
```

`main.bicep` writes the SQL connection string into the vault as the secret `sql-conn` (so the deploy
principal also needs **Key Vault Secrets Officer** on the vault, granted the same way as step 3) and
the Container App declares it as a Key Vault reference:
`secrets: [ { name: 'sql-conn', keyVaultUrl: <secret>.properties.secretUri, identity: <app UAMI> } ]`,
with the container env var `ConnectionStrings__SQLServerConnectionString` bound to it via `secretRef`.
The platform resolves the reference at revision start using the app's identity, so no plaintext
Container App secret and no value in the deployment output.

## SQL: Entra-only authentication and the one-time database user

The SQL server in `main.bicep` uses **Entra-only authentication** (`azureADOnlyAuthentication: true`):
it has no SQL login and no password. Its administrator is the Entra group passed as
`sqlEntraAdminLogin` (display name) and `sqlEntraAdminObjectId` (object id); people who need to
administer the server join that group. The app connects as its user-assigned managed identity: the
connection string carries `Authentication=Active Directory Managed Identity;User Id=<UAMI client id>`
and no secret. That keyword needs the SqlClient Entra provider, which ships in
`Microsoft.Data.SqlClient.Extensions.Azure`; reference `MMCA.Common.Azure`, which brings it.

The identity is a **contained user of the app's own database**, never a server administrator. Create
it once, after the first `main.bicep` deploy has created the database and before the app first starts
against it (until then the app cannot sign in). The T-SQL is data-plane work the deploy principal
cannot do, so a member of the Entra admin group runs it, connected to the app database (`MyApp`), not
to `master`:

```bash
# As a member of the SQL Entra admin group (az login first), from a network the server admits.
sqlcmd -S <server>.database.windows.net -d MyApp --authentication-method ActiveDirectoryDefault -Q "
CREATE USER [myapp-prod-apps-identity] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [myapp-prod-apps-identity];
ALTER ROLE db_datawriter ADD MEMBER [myapp-prod-apps-identity];
ALTER ROLE db_ddladmin ADD MEMBER [myapp-prod-apps-identity];"
```

The user name is the managed identity's name from bootstrap step 1. `db_ddladmin` is there only
because the service applies its own migrations at startup (see Migrations below); a host that
migrates from a separate step leaves it out. Repeat the statements per database when the app has more
than one.

**Network access.** `main.bicep` declares no SQL firewall rule, and deliberately not the `0.0.0.0`
"allow Azure services" rule, which admits connection attempts from every Azure-hosted machine in every
tenant. Until you add a rule for your own network nothing can reach the server, the app included. The
commented placeholder next to the SQL resources in `main.bicep` shows the two shapes: a private
endpoint or virtual network rule for the app subnet (preferred, then set `publicNetworkAccess` to
`Disabled`), or a firewall rule for the environment's fixed egress address. For the one-time T-SQL
above, a temporary firewall rule for your own client address is enough; remove it afterwards.

Step 3 is also the only grant `AddAzureKeyVaultConfiguration()` (MMCA.Common.Azure) needs: the app opts
in by referencing MMCA.Common.Azure, calling `builder.AddAzureKeyVaultConfiguration()` and setting
`KeyVault:Uri` to the vault URI, and the same managed identity reads the secrets as configuration. A
host that sets `KeyVault:Uri` without the call fails at startup, naming it. Name the secrets with a double dash where the configuration key has a colon
(`ConnectionStrings--Default` binds to `ConnectionStrings:Default`).

## OIDC for the GitHub deploy identity (no stored cloud secret)

CD authenticates to Azure with a **federated credential** — GitHub mints a short-lived OIDC token per
run; there is no client secret in the repo.

```bash
APP_ID=<deploy app registration appId>
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<org>/<repo>:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

The workflow then logs in with no standing credential:

```yaml
permissions:
  id-token: write      # required for OIDC
  contents: read
jobs:
  deploy:
    steps:
      - uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
      - run: az deployment group create -g $RG -f infra/foundation.bicep -p environmentName=prod
      - run: |  # build & push the image to ACR (via the deploy identity's AcrPush), then:
          az deployment group create -g $RG -f infra/main.bicep \
            -p environmentName=prod acrName=$ACR logAnalyticsName=$LOGS containerImage=$IMAGE \
               appIdentityResourceId=$UAMI_ID sqlEntraAdminLogin=$SQL_ADMIN_GROUP \
               sqlEntraAdminObjectId=$SQL_ADMIN_GROUP_ID ...
```

## Alerting and its runbook

`main.bicep` provisions four SLO log-search alerts (failed requests, server response time,
availability, and language-model token spend) over the Log Analytics workspace, wired to the action
group when `alertEmail` is set. Each one has a triage section in `OPERATIONS.md` next to this file:
what it means, what to look at first, how to recover, when to escalate. The pairing is a build gate,
not a convention: `ObservabilityConventionTestsBase` (`MMCA.Common.Testing.Architecture`) parses
`sloAlertSpecs` out of the template and the `###` headings out of the runbook, and fails the build
when an alert has no section, a section names an alert that no longer exists, or a heading's severity
disagrees with the template. `OPERATIONS.md` closes with the two steps for wiring that gate into your
own repository; the thresholds in the sample are placeholders, so size them from your own baseline
before the first page.

## Migrations

Each service applies its own migrations at startup (`ApplicationSettings.DatabaseInitStrategy=Migrate`,
`minReplicas: 1` guarantees a single applier) — there is no separate sqlcmd deploy step that could
race the container's startup `Migrate()`. (Worked example + rationale: MMCA.ADC/CLAUDE.md.)

## Post-deploy smoke gate + auto-rollback

After a release, probe the live endpoints with retries; if the new revision does not serve, revert
each Container App to its previous revision and fail the run — so a bad release never stays live.

```bash
# probe (retry) the gateway/app health + JWKS + a root page; on failure:
az containerapp revision copy -g $RG -n myapp-prod-api --from-revision <last-good-revision>
```

## Environment parity & cost

- **Parity:** local dev runs the same topology via Aspire (`AddServiceDefaults`); prod swaps in
  Azure-managed SQL/Service Bus/Redis via config, not code.
- **Cost (§31):** every resource carries the `commonTags` set (attribution), a monthly budget alerts
  at 80%/100%, and a scheduled read-only cost-guard can fail if a temporary surge wasn't reverted
  (sample in [the published COST guide](https://ivanball.github.io/docs/guides/common-COST.html); worked example: MMCA.ADC `cost-guard.yml`).
- **Resilience (§29):** define RTO/RPO and drill restores — see [the published RESILIENCE guide](https://ivanball.github.io/docs/guides/common-RESILIENCE.html).
