# Reference Deployment (rubric §17)

MMCA.Common is a library and cannot deploy itself, so the §17 machinery (IaC, CD, OIDC/managed
identity) lives in the consumer apps. This folder is a **distilled reference** of that machinery so a
new app built on the framework can be deployed reproducibly, with no long-lived credentials and with
spend attributed from day one. The full, production-proven version is **MMCA.ADC/infra/** +
**MMCA.ADC/.github/workflows/deploy.yml**; this sample is the minimal single-service shape.

Files:
- [`foundation.bicep`](foundation.bicep) — long-lived resources (Log Analytics + ACR). Deploy once.
- [`main.bicep`](main.bicep) — per-release: Container App (pulls from ACR via managed identity),
  SQL, Key Vault, App Insights, cost tags, budget + alerts.

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
               appIdentityResourceId=$UAMI_ID sqlAdminLogin=$SQL_USER sqlAdminPassword=$SQL_PWD ...
```

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
