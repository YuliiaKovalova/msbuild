---
name: "Build Failure Analysis (command)"
description: >-
  Rerun the build-failure analysis on a pull request when a maintainer
  comments `/analyze-build-failure`. Reuses the binlog already produced by
  the most recent failed Azure DevOps (DevDiv) build for the PR's head SHA
  — does NOT run `./build.sh` again. Useful when a previous run was
  cancelled, the analysis comment was dismissed, or the agent needs another
  pass after a force-push that triggered a fresh AzDO build.

on:
  slash_command:
    name: analyze-build-failure
    events: [pull_request_comment]
    strategy: centralized
  roles: [admin, maintainer, write]
  reaction: "eyes"

permissions:
  contents: read
  pull-requests: read
  checks: read
  # OIDC token used to federate into Azure AD → AzDO REST.
  id-token: write

concurrency:
  group: build-failure-analysis-${{ github.event.issue.number }}
  cancel-in-progress: true

env:
  BINLOG_MCP_VERSION: '1.0.0-preview.26272.1'
  NUGET_MCP_VERSION: '1.4.3'
  AZDO_ORG: 'devdiv'
  AZDO_PROJECT: 'DevDiv'
  AZDO_CHECK_APP_SLUG: 'azure-pipelines'
  AZDO_RESOURCE_GUID: '499b84ac-1321-427f-aa17-267ca6975798'

timeout-minutes: 15

network:
  allowed:
    - defaults
    - dotnet
    - "dev.azure.com"
    - "*.dev.azure.com"
    - "*.blob.core.windows.net"

imports:
  - shared/build-failure-analysis-shared.md

# Deterministic setup that runs before the AI agent starts. By the time the
# agent boots: dotnet is on PATH, the binlog has been downloaded from the
# most recent failed AzDO build for this PR (NOT rebuilt locally), the
# binlog path and build outcome are exported as `GH_AW_*` env vars,
# `binlog-mcp` is installed, and the binlog data has been dumped to
# `/tmp/binlog-data/*.json` files for the agent to `cat`.
steps:
  # `pull_request_comment` events use the `issues` payload, so `github.sha`
  # is the default branch tip — NOT the PR head. Always resolve the real PR
  # head SHA via the API so permalinks and inline comment placement match
  # the PR.
  - name: Resolve PR head SHA
    id: resolve-pr-sha
    env:
      GH_TOKEN: ${{ github.token }}
      GH_AW_GITHUB_REPOSITORY: ${{ github.repository }}
      GH_AW_GITHUB_EVENT_ISSUE_NUMBER: ${{ github.event.issue.number }}
    run: |
      SHA=$(gh api "repos/${GH_AW_GITHUB_REPOSITORY}/pulls/${GH_AW_GITHUB_EVENT_ISSUE_NUMBER}" --jq .head.sha)
      echo "sha=$SHA" >> "$GITHUB_OUTPUT"

  - name: Azure login (federated identity)
    uses: azure/login@v2
    with:
      client-id: ${{ secrets.AZDO_FEDERATED_CLIENT_ID }}
      tenant-id: ${{ secrets.AZDO_FEDERATED_TENANT_ID }}
      allow-no-subscriptions: true

  # Find the latest *failed* AzDO check on the PR head SHA. We use GitHub's
  # check-runs API (rather than AzDO build search) because it's faster and
  # already scoped to this commit. The check_run's `external_id` is the
  # AzDO build ID; `details_url` is the AzDO build results page.
  - name: Find latest failed AzDO build for PR head
    id: find-build
    env:
      GH_TOKEN: ${{ github.token }}
      REPO: ${{ github.repository }}
      HEAD_SHA: ${{ steps.resolve-pr-sha.outputs.sha }}
    run: |
      set -euo pipefail
      RUNS=$(gh api "repos/${REPO}/commits/${HEAD_SHA}/check-runs?per_page=100")
      MATCH=$(echo "$RUNS" | jq -c --arg slug "$AZDO_CHECK_APP_SLUG" '
        [.check_runs[]
          | select(.app.slug == $slug)
          | select(.conclusion == "failure")]
        | sort_by(.completed_at) | reverse | .[0] // empty')
      if [ -z "$MATCH" ] || [ "$MATCH" = "null" ]; then
        echo "::warning::No failed Azure Pipelines check found on $HEAD_SHA."
        echo "build-id=" >> "$GITHUB_OUTPUT"
        exit 0
      fi
      EXTERNAL_ID=$(echo "$MATCH" | jq -r .external_id)
      DETAILS_URL=$(echo "$MATCH" | jq -r .details_url)
      BUILD_ID=""
      if [[ "$EXTERNAL_ID" =~ ^[0-9]+$ ]]; then
        BUILD_ID="$EXTERNAL_ID"
      elif [ -n "$DETAILS_URL" ]; then
        BUILD_ID=$(echo "$DETAILS_URL" | grep -oE 'buildId=[0-9]+' | head -1 | cut -d= -f2)
      fi
      echo "build-id=$BUILD_ID"     >> "$GITHUB_OUTPUT"
      echo "details-url=$DETAILS_URL" >> "$GITHUB_OUTPUT"
      echo "Selected failed AzDO build: $BUILD_ID ($DETAILS_URL)"

  - name: Download AzDO binlog
    id: fetch-binlog
    if: steps.find-build.outputs.build-id != ''
    continue-on-error: true
    env:
      BUILD_ID: ${{ steps.find-build.outputs.build-id }}
    run: |
      set -euo pipefail
      TOKEN=$(az account get-access-token --resource "$AZDO_RESOURCE_GUID" --query accessToken -o tsv)
      AUTH_HEADER="Authorization: Bearer $TOKEN"

      mkdir -p /tmp/azdo-artifact /tmp/azdo-binlog
      ARTIFACTS_JSON=$(curl -sS -H "$AUTH_HEADER" \
        "https://dev.azure.com/${AZDO_ORG}/${AZDO_PROJECT}/_apis/build/builds/${BUILD_ID}/artifacts?api-version=7.1")
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
      curl -sS -L -H "$AUTH_HEADER" -o /tmp/azdo-artifact/logs.zip "$DOWNLOAD_URL"
      unzip -q -o /tmp/azdo-artifact/logs.zip -d /tmp/azdo-binlog
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
  # `./build.sh`.
  - name: Synthesize build context
    id: build
    if: always()
    env:
      FETCHED: ${{ steps.fetch-binlog.outputs.found }}
    run: |
      if [ "$FETCHED" = "true" ]; then
        echo "outcome=failure" >> "$GITHUB_OUTPUT"
      else
        echo "outcome=success" >> "$GITHUB_OUTPUT"
      fi

  - name: Put dotnet on the path
    if: always()
    run: echo "$PWD/.dotnet" >> $GITHUB_PATH

  # Compatibility alias: downstream steps and the agent prompt key off
  # `steps.find-binlog.outputs.{found,path}`. Re-emit them from
  # `fetch-binlog` so the downstream surface is unchanged.
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

  - name: Export agent context
    env:
      GH_AW_STEPS_BUILD_OUTCOME: ${{ steps.build.outcome }}
      GH_AW_BINLOG_PATH_VALUE: ${{ steps.find-binlog.outputs.path }}
      GH_AW_GITHUB_EVENT_ISSUE_NUMBER: ${{ github.event.issue.number }}
      GH_AW_PR_HEAD_SHA_VALUE: ${{ steps.resolve-pr-sha.outputs.sha || github.sha }}
      GH_AW_AZDO_BUILD_ID_VALUE: ${{ steps.find-build.outputs.build-id }}
      GH_AW_AZDO_BUILD_URL_VALUE: ${{ steps.find-build.outputs.details-url }}
      GH_AW_GITHUB_WORKSPACE: ${{ github.workspace }}
    run: |
      {
        echo "GH_AW_BUILD_OUTCOME=${GH_AW_STEPS_BUILD_OUTCOME}"
        echo "GH_AW_BINLOG_PATH=${GH_AW_BINLOG_PATH_VALUE}"
        echo "GH_AW_PR_NUMBER=${GH_AW_GITHUB_EVENT_ISSUE_NUMBER}"
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
-->
