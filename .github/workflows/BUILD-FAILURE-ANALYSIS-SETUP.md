# Build-failure-analysis: Path A (AzDO binlog reuse) — repo setup

The `build-failure-analysis.md` and `build-failure-analysis-command.md`
workflows download the binlog that the failing **DevDiv AzDO PR pipeline**
already produced, instead of re-running `./build.sh --binaryLog` on the GH
Actions runner. This cuts ~30–60 min of duplicate CI per failing PR sync.

For this to work, two one-time setup steps are required.

## 1. Azure AD app with federated identity

The workflows authenticate to AzDO using **OIDC federated identity** — no
client secrets stored in the repo. You need an Azure AD application
(service principal) with:

- **Federated credentials** trusting GitHub's OIDC issuer (`https://token.actions.githubusercontent.com`) and a subject pattern that matches the workflow runs:
  - For `check_run` and `workflow_dispatch` events on the default branch (where these workflows live):
    `repo:dotnet/msbuild:ref:refs/heads/main`
  - For `pull_request_comment` slash-command runs (which check out the default branch and use base-branch context):
    `repo:dotnet/msbuild:ref:refs/heads/main`
  - Add additional release-branch subjects (`refs/heads/vs*`) if the workflows are enabled there too.

- **AzDO permission**: the service principal must be added to the DevDiv AzDO
  project (`https://dev.azure.com/devdiv/DevDiv`) with at least:
  - **Read** on the `MSBuild` build pipeline (or `Project Build Service` group membership scoped to read-only)
  - **Read** on build artifacts

The Azure AD app's `clientId` and `tenantId` go into the secrets below.

## 2. Repository secrets

Add to **Settings → Secrets and variables → Actions** (or `gh secret set`):

| Secret | Value | Notes |
|---|---|---|
| `AZDO_FEDERATED_CLIENT_ID` | Azure AD app's Application (client) ID | Public-ish — not a credential by itself |
| `AZDO_FEDERATED_TENANT_ID` | Azure AD tenant ID | Microsoft tenant for dotnet/msbuild |

No client secret is needed — federated identity replaces it.

## Verification

After secrets are in place, push a commit that intentionally breaks the
build (e.g., delete a `using` from `Microsoft.Build.Tasks`). Wait for the
DevDiv AzDO PR pipeline to fail. Within ~60 seconds of the AzDO check
posting "failure" back to GitHub, the **Build Failure Analysis** workflow
should run, with the **`Build with binary log`** step **absent** and a new
**`Download AzDO binlog`** step showing
`Binlog reused from AzDO build NNNN: /tmp/azdo-binlog/.../Build.binlog`.

Compare runtime against the rebuild-based variant:

| Variant | GH Actions runtime per failing PR sync |
|---|---|
| Old (`./build.sh --binaryLog`) | ~3–5 min (build) + ~1 min (tool install + dump) + ~1–2 min (agent) = **5–8 min** |
| New (AzDO binlog reuse) | ~5 s (download) + ~1 min (tool install + dump) + ~1–2 min (agent) = **2–3 min** |

## Fork-PR behavior

DevDiv pipelines do not run on fork PRs, so the `check_run` trigger never
fires for forks → the workflow is implicitly fork-safe. The previous design
also skipped forks (`forks: []`), so this is not a regression.

If you want fork-PR analysis back, you'd need a fallback path that runs
`./build.sh --binaryLog` locally only when no AzDO check is detected — but
that defeats the purpose of this rewrite and is out of scope.

## Troubleshooting

**`AADSTS70021: No matching federated identity record found`**
The subject in the Azure AD federated credential doesn't match. Run the
workflow once and copy the exact subject from the error message into the
federated credential configuration.

**`TF400813: The user '...' is not authorized to access this resource`**
The service principal isn't in the DevDiv project or lacks Build (read)
permission. Add it via AzDO → Project Settings → Permissions.

**`No PostBuildLogs_* artifact found on build NNNN`**
The AzDO build failed before the publish-logs step ran (very early failure).
The workflow no-ops with a warning. Use the `/analyze-build-failure` slash
command after a subsequent build, or fall back to reading the build's task
log directly.
