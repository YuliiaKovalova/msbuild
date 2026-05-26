---
# Shared body for the build-failure-analysis workflows.
#
# Imported by build-failure-analysis.md (pull_request trigger) and
# build-failure-analysis-command.md (slash command). Keeps the prompt that
# delegates to the build-failure-analyst agent in one place. Per-trigger
# wiring (steps, env, mcp-servers, permissions) lives in each caller because
# gh-aw merges those fields from imports but each main workflow must still
# re-declare its top-level permissions.

description: "Shared body for build-failure-analysis workflows"
---

# Build Failure Analyst

Delegate to the `build-failure-analyst` agent defined at
`.github/agents/build-failure-analyst.agent.md` to analyze the binary log of
the build that just ran and post a PR review when (and only when) the build
failed.

## Instructions

1. Read the agent-context environment variables: `GH_AW_BUILD_OUTCOME`,
   `GH_AW_BINLOG_PATH`, `GH_AW_PR_NUMBER`, `GH_AW_PR_HEAD_SHA`,
   `GH_AW_AZDO_BUILD_ID`, `GH_AW_AZDO_BUILD_URL`, `GH_AW_WORKSPACE`.

2. If `GH_AW_BUILD_OUTCOME == 'success'`, the upstream AzDO build did not
   actually fail (or no binlog could be downloaded — same effect from this
   workflow's point of view). Call `noop` with the message
   `"AzDO build did not fail or no binlog available — no analysis required."`
   and stop.

3. Otherwise, launch the `build-failure-analyst` agent as a **background**
   task (`task` tool, `agent_type: "general-purpose"`,
   `model: "claude-opus-4.6"`, `mode: "background"`). In the sub-agent prompt
   include:
   - All seven `GH_AW_*` environment values verbatim so the sub-agent knows
     which binlog metadata to read, which AzDO build it came from, and where
     to post.
   - A reminder that the binlog was **downloaded from the failing AzDO build
     `GH_AW_AZDO_BUILD_ID`** at `GH_AW_AZDO_BUILD_URL` — NOT produced by a
     local `./build.sh` in this workflow. The sub-agent should reference the
     AzDO build URL in its summary comment so reviewers can cross-check
     against the canonical CI run.
   - A reminder that the pre-agent steps already dumped overview / errors /
     warnings to `/tmp/binlog-data/*.json` and that the sub-agent should
     start by `cat`ing those files via the `bash` tool.
   - A reminder that the parent workflow `noop`s immediately and that the
     sub-agent itself is responsible for calling `add_comment` (summary) and
     `create_pull_request_review_comment` (inline `suggestion` blocks).
   - A reminder that `submit_pull_request_review` is **not** a safe output
     for this workflow — inline comments stand alone.

4. **Immediately after launching the background task** — do NOT wait for it
   to finish and do NOT read its result — call `noop` with a brief status
   message such as
   `"Build-failure analyst launched in background for PR #N (reusing AzDO build $GH_AW_AZDO_BUILD_ID). It will post the analysis directly."`.
   Then stop.

> **Important**: Reading the background agent result would pull its entire
> conversation (including every binlog query and every source file it
> inspected) into your context. Do not call `read_agent` or any equivalent
> after calling `noop`.
