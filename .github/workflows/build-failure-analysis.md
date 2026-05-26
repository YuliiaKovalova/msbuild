---
name: "Build Failure Analysis"
description: >-
  When the Azure DevOps (DevDiv) PR build posts back a failed check, downloads
  the binlog already produced by that pipeline, then delegates to the
  `build-failure-analyst` agent (which reads JSON dumps produced from the
  binlog) to identify root causes, post a PR comment summarizing them, and
  attach inline `suggestion` blocks tied to the diff.

# This workflow is **advisory**, not gating:
#  - It posts an analysis comment / inline suggestions when the AzDO build fails.
#  - It does NOT mark the PR check as failing on its own.
#  - The deterministic build gate lives in the Azure DevOps pipeline itself;
#    this workflow exists to surface root-cause analysis directly on the PR
#    *without* paying the cost of a second build.
#
# Trigger model — "reuse, don't rebuild":
#   `on: check_run: types: [completed]` fires the moment the AzDO build's
#   GitHub Check completes. We filter to Azure Pipelines failures only, then
#   download the `PostBuildLogs_*` artifact the AzDO run already published
#   (no `./build.sh` here, saves ~30-60 min of CI per failing PR sync).
#   Fork PRs are skipped at the job-condition level because internal DevDiv
#   pipelines don't run on forks (no AzDO build => nothing to analyze).

on:
  check_run:
    types: [completed]
  workflow_dispatch:
    inputs:
      pr-number:
        description: "PR number to scope inline suggestion comments to (optional)"
        required: false
        type: string
      build-id:
        description: "AzDO build ID to fetch the binlog from (optional; defaults to latest failed build for the PR head SHA)"
        required: false
        type: string
  # Manual reruns and dispatch invocations are restricted to repository
  # contributors. For a slash-command rerun path on PR comments, see
  # the companion `build-failure-analysis-command.md` workflow.
  roles: [admin, maintainer, write]
  reaction: "eyes"

permissions:
  contents: read
  pull-requests: read
  checks: read
  # OIDC token used to federate into Azure AD → AzDO REST. Required by
  # `azure/login@v2` below. The federated credential on the Azure AD app
  # must trust `repo:dotnet/msbuild:ref:refs/heads/*` (or PR-scoped subject).
  id-token: write

concurrency:
  # One run per PR; subsequent AzDO check completions cancel the in-flight
  # analysis so the latest failure is always what the agent comments on.
  # (Slash-command rerun lives in a separate workflow / separate group.)
  group: build-failure-analysis-${{ github.event.check_run.pull_requests[0].number || github.event.issue.number || inputs.pr-number || github.ref }}
  cancel-in-progress: true

env:
  BINLOG_MCP_VERSION: '1.0.0-preview.26272.1'
  NUGET_MCP_VERSION: '1.4.3'
  # AzDO coordinates for msbuild's PR build. These are repo-stable and
  # parameterized here so the prototype can be retargeted (e.g. to a
  # release-branch pipeline) without editing the steps below.
  AZDO_ORG: 'devdiv'
  AZDO_PROJECT: 'DevDiv'
  # Slug GitHub assigns to the Azure Pipelines GitHub App. Used to filter
  # `check_run` events down to the AzDO build only; everything else (CodeQL,
  # Dependabot, other gh-aw runs) is ignored.
  AZDO_CHECK_APP_SLUG: 'azure-pipelines'
  # Azure AD resource GUID for Azure DevOps. Tokens minted against this
  # resource authenticate REST calls to dev.azure.com.
  AZDO_RESOURCE_GUID: '499b84ac-1321-427f-aa17-267ca6975798'

timeout-minutes: 15

network:
  allowed:
    - defaults
    - dotnet
    # Required for AzDO REST + artifact download. Login/STS endpoint is
    # already covered by `defaults`.
    - "dev.azure.com"
    - "*.dev.azure.com"
    - "*.blob.core.windows.net"

imports:
  - shared/build-failure-analysis-shared.md

# Deterministic setup that runs before the AI agent starts. By the time the
# agent boots: dotnet is on PATH, the binlog has been downloaded from the
# failing AzDO build (NOT rebuilt locally), the binlog path and build outcome
# are exported as `GH_AW_*` env vars, `binlog-mcp` is installed, and the
# binlog data has been dumped to `/tmp/binlog-data/*.json` for the agent to
# `cat`.
steps:
  # Gate the whole job on the check_run event being:
  #   1. From Azure Pipelines (not CodeQL, not another gh-aw workflow)
  #   2. Concluded as `failure` (success / neutral / skipped → nothing to analyze)
  #   3. Attached to at least one PR (excludes branch-only batched CI builds)
  # `workflow_dispatch` bypasses the filter so maintainers can rerun manually.
  - name: Gate on Azure Pipelines failure
    id: gate
    if: always()
    env:
      EVENT_NAME: ${{ github.event_name }}
      CHECK_APP_SLUG: ${{ github.event.check_run.app.slug }}
      CHECK_CONCLUSION: ${{ github.event.check_run.conclusion }}
      CHECK_PR_COUNT: ${{ github.event.check_run.pull_requests && toJSON(github.event.check_run.pull_requests) || '[]' }}
      EXPECTED_APP_SLUG: ${{ env.AZDO_CHECK_APP_SLUG }}
    run: |
      if [ "$EVENT_NAME" = "workflow_dispatch" ]; then
        echo "proceed=true" >> "$GITHUB_OUTPUT"
        echo "Manual dispatch — bypassing gate."
        exit 0
      fi
      if [ "$CHECK_APP_SLUG" != "$EXPECTED_APP_SLUG" ]; then
        echo "proceed=false" >> "$GITHUB_OUTPUT"
        echo "Skipping: check_run is from '$CHECK_APP_SLUG', not '$EXPECTED_APP_SLUG'."
        exit 0
      fi
      if [ "$CHECK_CONCLUSION" != "failure" ]; then
        echo "proceed=false" >> "$GITHUB_OUTPUT"
        echo "Skipping: check_run conclusion is '$CHECK_CONCLUSION', not 'failure'."
        exit 0
      fi
      PR_COUNT=$(echo "$CHECK_PR_COUNT" | jq 'length')
      if [ "$PR_COUNT" -lt 1 ]; then
        echo "proceed=false" >> "$GITHUB_OUTPUT"
        echo "Skipping: check_run not associated with a PR (branch build)."
        exit 0
      fi
      echo "proceed=true" >> "$GITHUB_OUTPUT"

  # Federated identity → Azure AD → AzDO REST token. No client secret stored
  # in the repo; the Azure AD app must have a federated credential trusting
  # `repo:dotnet/msbuild:*` (or a narrower PR subject). The DevDiv project
  # requires authentication for both build queries and artifact downloads.
  - name: Azure login (federated identity)
    if: steps.gate.outputs.proceed == 'true'
    uses: azure/login@v2
    with:
      client-id: ${{ secrets.AZDO_FEDERATED_CLIENT_ID }}
      tenant-id: ${{ secrets.AZDO_FEDERATED_TENANT_ID }}
      allow-no-subscriptions: true

  # Resolve the AzDO build ID we'll be analyzing. Three sources, in priority:
  #   1. `inputs.build-id` (manual dispatch overrides everything)
  #   2. `check_run.external_id` is set to the AzDO build ID by Azure Pipelines
  #   3. Fall back to parsing the build URL out of `check_run.details_url`
  # Also resolves the PR head SHA we'll use for inline-comment placement.
  - name: Resolve AzDO build & PR context
    id: resolve
    if: steps.gate.outputs.proceed == 'true'
    env:
      EVENT_NAME: ${{ github.event_name }}
      INPUT_BUILD_ID: ${{ inputs.build-id }}
      INPUT_PR_NUMBER: ${{ inputs.pr-number }}
      CHECK_EXTERNAL_ID: ${{ github.event.check_run.external_id }}
      CHECK_DETAILS_URL: ${{ github.event.check_run.details_url }}
      CHECK_HEAD_SHA: ${{ github.event.check_run.head_sha }}
      CHECK_PRS_JSON: ${{ github.event.check_run.pull_requests && toJSON(github.event.check_run.pull_requests) || '[]' }}
      GH_TOKEN: ${{ github.token }}
      REPO: ${{ github.repository }}
    run: |
      set -euo pipefail

      # ---- Build ID --------------------------------------------------------
      BUILD_ID=""
      if [ -n "${INPUT_BUILD_ID:-}" ]; then
        BUILD_ID="$INPUT_BUILD_ID"
      elif [ -n "${CHECK_EXTERNAL_ID:-}" ] && [[ "$CHECK_EXTERNAL_ID" =~ ^[0-9]+$ ]]; then
        BUILD_ID="$CHECK_EXTERNAL_ID"
      elif [ -n "${CHECK_DETAILS_URL:-}" ]; then
        BUILD_ID=$(echo "$CHECK_DETAILS_URL" | grep -oE 'buildId=[0-9]+' | head -1 | cut -d= -f2)
      fi

      # ---- PR number + head SHA -------------------------------------------
      PR_NUMBER=""
      PR_HEAD_SHA=""
      if [ "$EVENT_NAME" = "workflow_dispatch" ] && [ -n "${INPUT_PR_NUMBER:-}" ]; then
        PR_NUMBER="$INPUT_PR_NUMBER"
        PR_HEAD_SHA=$(gh api "repos/${REPO}/pulls/${PR_NUMBER}" --jq .head.sha)
      else
        PR_NUMBER=$(echo "$CHECK_PRS_JSON" | jq -r '.[0].number // empty')
        PR_HEAD_SHA="${CHECK_HEAD_SHA:-}"
      fi

      # On workflow_dispatch with build-id but no pr-number, derive PR from
      # the build's source SHA.
      if [ -z "$PR_NUMBER" ] && [ -n "$BUILD_ID" ]; then
        TOKEN=$(az account get-access-token --resource "$AZDO_RESOURCE_GUID" --query accessToken -o tsv)
        SRC_SHA=$(curl -sS -H "Authorization: Bearer $TOKEN" \
          "https://dev.azure.com/${AZDO_ORG}/${AZDO_PROJECT}/_apis/build/builds/${BUILD_ID}?api-version=7.1" \
          | jq -r .sourceVersion)
        if [ -n "$SRC_SHA" ] && [ "$SRC_SHA" != "null" ]; then
          PR_NUMBER=$(gh api "repos/${REPO}/commits/${SRC_SHA}/pulls" --jq '.[0].number // empty')
          PR_HEAD_SHA="$SRC_SHA"
        fi
      fi

      echo "build-id=$BUILD_ID"           >> "$GITHUB_OUTPUT"
      echo "pr-number=$PR_NUMBER"         >> "$GITHUB_OUTPUT"
      echo "pr-head-sha=$PR_HEAD_SHA"     >> "$GITHUB_OUTPUT"

      if [ -z "$BUILD_ID" ]; then
        echo "::warning::Could not determine AzDO build ID; downstream steps will no-op."
      fi

  # Download the binlog the AzDO build already produced. The arcade
  # `publish-logs.yml` template publishes binlogs under artifact name
  # `PostBuildLogs_<StageLabel>_<JobLabel>_Attempt<N>` (see
  # eng/common/core-templates/steps/publish-logs.yml). We pick the largest
  # such artifact (= the full repo build, not a sub-stage) and extract its
  # most-recently-modified `*.binlog`.
  - name: Download AzDO binlog
    id: fetch-binlog
    if: steps.gate.outputs.proceed == 'true' && steps.resolve.outputs.build-id != ''
    continue-on-error: true
    env:
      BUILD_ID: ${{ steps.resolve.outputs.build-id }}
    run: |
      set -euo pipefail
      TOKEN=$(az account get-access-token --resource "$AZDO_RESOURCE_GUID" --query accessToken -o tsv)
      AUTH_HEADER="Authorization: Bearer $TOKEN"

      mkdir -p /tmp/azdo-artifact /tmp/azdo-binlog

      # 1. List artifacts on the failing build.
      ARTIFACTS_JSON=$(curl -sS -H "$AUTH_HEADER" \
        "https://dev.azure.com/${AZDO_ORG}/${AZDO_PROJECT}/_apis/build/builds/${BUILD_ID}/artifacts?api-version=7.1")

      # 2. Pick a PostBuildLogs artifact (largest = most complete build leg).
      ARTIFACT=$(echo "$ARTIFACTS_JSON" \
        | jq -r '[.value[] | select(.name | startswith("PostBuildLogs_"))]
                  | sort_by(.resource.properties.artifactsize | tonumber? // 0)
                  | reverse | .[0] // empty')
      if [ -z "$ARTIFACT" ] || [ "$ARTIFACT" = "null" ]; then
        echo "::warning::No PostBuildLogs_* artifact found on build $BUILD_ID."
        echo "found=false" >> "$GITHUB_OUTPUT"
        exit 0
      fi
      ARTIFACT_NAME=$(echo "$ARTIFACT" | jq -r .name)
      DOWNLOAD_URL=$(echo "$ARTIFACT"  | jq -r .resource.downloadUrl)
      echo "Selected artifact: $ARTIFACT_NAME"

      # 3. Download and unzip.
      curl -sS -L -H "$AUTH_HEADER" -o /tmp/azdo-artifact/logs.zip "$DOWNLOAD_URL"
      unzip -q -o /tmp/azdo-artifact/logs.zip -d /tmp/azdo-binlog

      # 4. Pick the most-recently-modified binlog (covers stage-specific
      # binlogs like `Build.binlog`, `Restore.binlog`).
      BINLOG=$(find /tmp/azdo-binlog -name '*.binlog' -type f -printf '%T@ %p\n' \
        | sort -rn | head -1 | cut -d' ' -f2-)
      if [ -n "$BINLOG" ] && [ -f "$BINLOG" ]; then
        BINLOG=$(realpath "$BINLOG")
        echo "found=true"   >> "$GITHUB_OUTPUT"
        echo "path=$BINLOG" >> "$GITHUB_OUTPUT"
        echo "Binlog reused from AzDO build $BUILD_ID: $BINLOG"
      else
        echo "::warning::Downloaded $ARTIFACT_NAME but it contains no *.binlog."
        echo "found=false" >> "$GITHUB_OUTPUT"
      fi

  # Shim: synthesize the (build outcome, binlog path) the downstream agent
  # context expects, but sourced from the AzDO build instead of a local
  # `./build.sh`. `outcome=failure` is hard-coded because we only got here
  # via a failed `check_run`; the gate already filtered out successes.
  - name: Synthesize build context
    id: build
    if: always()
    env:
      PROCEED: ${{ steps.gate.outputs.proceed }}
      FETCHED: ${{ steps.fetch-binlog.outputs.found }}
    run: |
      if [ "$PROCEED" = "true" ] && [ "$FETCHED" = "true" ]; then
        echo "outcome=failure" >> "$GITHUB_OUTPUT"
      else
        echo "outcome=success" >> "$GITHUB_OUTPUT"
      fi

  - name: Put dotnet on the path
    if: always()
    run: echo "$PWD/.dotnet" >> $GITHUB_PATH

  # Compatibility alias: downstream steps and the agent prompt key off
  # `steps.find-binlog.outputs.{found,path}` (the historical step name from
  # the rebuild-based design). Re-emit them from `fetch-binlog` so the
  # downstream surface is unchanged.
  - name: Locate binlog
    id: find-binlog
    if: always()
    env:
      FETCHED: ${{ steps.fetch-binlog.outputs.found }}
      FETCHED_PATH: ${{ steps.fetch-binlog.outputs.path }}
    run: |
      echo "found=${FETCHED:-false}" >> "$GITHUB_OUTPUT"
      echo "path=${FETCHED_PATH:-}"  >> "$GITHUB_OUTPUT"

  - name: Install binlog-mcp
    if: steps.build.outcome == 'failure' && steps.find-binlog.outputs.found == 'true'
    run: |
      mkdir -p /tmp/binlog-tool
      cat > /tmp/binlog-tool/nuget.config <<'EOF'
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <packageSources>
          <clear />
          <add key="dotnet-tools"
               value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json" />
        </packageSources>
      </configuration>
      EOF
      dotnet tool install --global Microsoft.AITools.BinlogMcp \
        --configfile /tmp/binlog-tool/nuget.config \
        --version "$BINLOG_MCP_VERSION"
      echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"

  - name: Install NuGet MCP Server
    if: steps.build.outcome == 'failure' && steps.find-binlog.outputs.found == 'true'
    continue-on-error: true
    run: dotnet tool install --global NuGet.Mcp.Server --version "$NUGET_MCP_VERSION"

  - name: Dump binlog as JSON
    if: steps.build.outcome == 'failure' && steps.find-binlog.outputs.found == 'true'
    continue-on-error: true
    env:
      BINLOG_PATH: ${{ steps.find-binlog.outputs.path }}
    run: |
      mkdir -p /tmp/binlog-data
      timeout 180 dotnet run --project .github/workflows/scripts/DumpBinlog -- \
        "$BINLOG_PATH" \
        /tmp/binlog-data

  # PR head SHA and PR number were resolved in the `resolve` step at the top
  # of the job (covers `check_run` events, `workflow_dispatch` with a PR
  # number, and `workflow_dispatch` with only a build ID). Re-export here so
  # the shared agent-context prompt picks them up via the same `GH_AW_*`
  # variables it always has.
  - name: Export agent context
    env:
      GH_AW_STEPS_BUILD_OUTCOME: ${{ steps.build.outcome }}
      GH_AW_BINLOG_PATH_VALUE: ${{ steps.find-binlog.outputs.path }}
      GH_AW_PR_NUMBER_VALUE: ${{ steps.resolve.outputs.pr-number }}
      GH_AW_PR_HEAD_SHA_VALUE: ${{ steps.resolve.outputs.pr-head-sha || github.sha }}
      GH_AW_AZDO_BUILD_ID_VALUE: ${{ steps.resolve.outputs.build-id }}
      GH_AW_AZDO_BUILD_URL_VALUE: ${{ github.event.check_run.details_url }}
      GH_AW_GITHUB_WORKSPACE: ${{ github.workspace }}
    run: |
      {
        echo "GH_AW_BUILD_OUTCOME=${GH_AW_STEPS_BUILD_OUTCOME}"
        echo "GH_AW_BINLOG_PATH=${GH_AW_BINLOG_PATH_VALUE}"
        echo "GH_AW_PR_NUMBER=${GH_AW_PR_NUMBER_VALUE}"
        echo "GH_AW_PR_HEAD_SHA=${GH_AW_PR_HEAD_SHA_VALUE}"
        echo "GH_AW_AZDO_BUILD_ID=${GH_AW_AZDO_BUILD_ID_VALUE}"
        echo "GH_AW_AZDO_BUILD_URL=${GH_AW_AZDO_BUILD_URL_VALUE}"
        echo "GH_AW_WORKSPACE=${GH_AW_GITHUB_WORKSPACE}"
      } >> "$GITHUB_ENV"

tools:
  github:
    toolsets: [pull_requests, repos, checks]
  bash:
    - "cat"
    - "head"
    - "tail"
    - "grep"
    - "wc"
    - "sort"
    - "uniq"
    - "ls"
    - "find"
    - "dotnet"

safe-outputs:
  add-comment:
    max: 1
    hide-older-comments: true
  create-pull-request-review-comment:
    max: 10
  noop:
    report-as-issue: false
---

<!--
  Body provided by shared/build-failure-analysis-shared.md.

  All build-failure analysis expertise (binlog parsing, error grouping,
  suggestion authoring) lives in the reusable agent at
  .github/agents/build-failure-analyst.agent.md.
-->
