---
name: signrelay-ci
description: >-
  Integrate SignRelay remote Authenticode / code signing into a CI build pipeline
  (GitHub Actions, MSBuild, AppVeyor, shell). Use when the user asks to adjust the
  build pipeline for SignRelay, signtool, Authenticode, code signing, or signing
  binaries without putting the certificate on the CI runner.
---

# SignRelay CI integration

Remote code signing: CI uploads binaries via `signrelay submit`; a Windows agent runs `signtool`; signed artifacts download back. Keys never live on the runner.

## Procedure

1. Confirm preconditions: relay URL, `SIGN_RELAY_CI_TOKEN` (= server `SignRelay__CiToken`), agent online. Do not invent server URLs or tokens.
2. Prefer a **reference**, not a hand-rolled recipe:
   - GitHub Actions → `uses: nefarius/SignRelay@v1` (see [references/github-actions.md](references/github-actions.md))
   - `dotnet publish` → `Nefarius.Tools.SignRelay.MSBuild` (see [references/msbuild.md](references/msbuild.md))
   - AppVeyor → [references/appveyor.md](references/appveyor.md)
   - Else → raw CLI (see [references/cli.md](references/cli.md))
3. Wire the secret; never commit tokens.
4. Self-check with `--dry-run` when a live relay is unavailable.
5. Full reference: https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md

## Hard constraints (never violate)

- Only CLI verb: **`submit`**. No `sign`, `sign-file`, `upload`.
- CLI takes **explicit file paths** — no globbing inside `signrelay`. Expand globs in the shell, Action, or MSBuild items.
- Require exactly one of **`--output`** or **`--in-place`**.
- **No** cert/thumbprint/subject/timestamp flags in CI (agent-side only).
- `--timeout` ≤ server `JobTimeToLive` (default 2h) and ≤ proxy SSE read timeout.
- CI token ≠ agent token. Pair `SIGN_RELAY_CI_TOKEN` with `SignRelay__CiToken` only.
- No MCP server exists for SignRelay; do not invent one.

## Install this skill

Copy this folder into a consumer repo:

- Cursor: `.cursor/skills/signrelay-ci/`
- Claude Code: `.claude/skills/signrelay-ci/`
