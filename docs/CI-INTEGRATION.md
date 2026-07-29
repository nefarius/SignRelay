# SignRelay CI integration

Point a pipeline (or an AI agent editing a pipeline) at this document. Server/agent install is out of scope here — see [DEPLOYMENT.md](DEPLOYMENT.md) and [AGENT-SETUP.md](AGENT-SETUP.md).

**One-line goal:** after build/publish, call `signrelay submit` (or the GitHub Action / MSBuild wrapper) so binaries are Authenticode-signed on a Windows agent without putting the signing key on the CI runner.

## Preconditions

| Requirement | Notes |
| --- | --- |
| Relay reachable | HTTPS base URL; `GET /health` succeeds from the runner |
| CI token | Secret matching server `SignRelay__CiToken` (≥ 32 chars in Production) |
| Agent online | At least one Windows agent with matching `SignRelay__AgentToken` and a usable cert |
| Proxy SSE timeouts | Reverse-proxy **read** timeout ≥ longest expected job (often 30–60 minutes) — see [DEPLOYMENT.md](DEPLOYMENT.md) |
| .NET on runner | SDK or runtime that can run the global tool (same major as published tool; see releases) |

## Choose an integration

| Approach | Use when |
| --- | --- |
| **GitHub Actions composite** (`nefarius/SignRelay`) | Workflows on GitHub; want glob expansion and annotated exit codes |
| **MSBuild package** (`Nefarius.Tools.SignRelay.MSBuild`) | `dotnet publish` already in the pipeline; prefer property-driven opt-in |
| **Raw CLI** (`Nefarius.Tools.SignRelay`) | AppVeyor, GitLab, Azure DevOps, scripts, or anything else |

There is **no MCP server** for SignRelay. Signing is a CLI (or Action/MSBuild) invocation at pipeline time — not a runtime tool-call for coding agents.

## GitHub Actions

Pin a release tag or a floating major tag once you maintain `v1`:

```yaml
- uses: actions/setup-dotnet@v6
  with:
    global-json-file: global.json   # or dotnet-version: '10.0.x'

- name: Sign with SignRelay
  uses: nefarius/SignRelay@v1
  with:
    server: https://relay.example.com
    token: ${{ secrets.SIGN_RELAY_CI_TOKEN }}
    files: |
      ./artifacts/MyApp.exe
      ./artifacts/*.dll
    output: ./signed
    # in-place: true   # mutually exclusive with output
    # timeout: '00:45:00'
    # tool-version: '1.2.3'   # pin Nefarius.Tools.SignRelay
```

Dry-run (no network) to validate globs and args:

```yaml
- uses: nefarius/SignRelay@v1
  with:
    server: https://relay.example.com
    token: dry-run-token-0123456789abcdef0123456789
    files: ./artifacts/MyApp.exe
    output: ./signed
    dry-run: true
```

Action inputs: `server`, `token`, `files` (multiline; **globs expanded by the action**), `output` \| `in-place`, `timeout`, `tool-version`, `dry-run`, `allow-insecure`, `skip-tool-install`.

Outputs: `signed-count`, `output-path`.

## MSBuild

```xml
<ItemGroup>
  <PackageReference Include="Nefarius.Tools.SignRelay.MSBuild" Version="*" PrivateAssets="all" />
</ItemGroup>
```

Install the CLI on the agent/runner first:

```bash
dotnet tool install --global Nefarius.Tools.SignRelay
```

Enable only in CI (default is off so local builds never hit the network):

```bash
dotnet publish -c Release \
  /p:SignRelayEnabled=true \
  /p:SignRelayServer=https://relay.example.com
```

Set `SIGN_RELAY_CI_TOKEN` (or `/p:SignRelayToken=...`). Signs **in-place** after `Publish` by default; override files with `<SignRelayFile Include="..." />`.

**Ordering:** if you pack or zip artifacts, run those targets **after** `SignRelaySign` so archives contain signed binaries. See the [package README](../src/SignRelay.MSBuild/README.md).

## AppVeyor

Secure variables are **not** available on pull-request builds — guard signing:

```yaml
install:
  - ps: dotnet tool install --global Nefarius.Tools.SignRelay

after_build:
  - ps: |
      if ($env:APPVEYOR_PULL_REQUEST_NUMBER) {
        Write-Host "Skipping SignRelay on PR build (secure vars unavailable)."
        exit 0
      }
      $files = @(Get-ChildItem -Path .\artifacts\*.exe -File | ForEach-Object { $_.FullName })
      if ($files.Count -eq 0) { throw "No files to sign under .\artifacts" }
      # AppVeyor job timeout must exceed --timeout (default 45m).
      & signrelay submit `
        --server https://relay.example.com `
        --token $env:SIGN_RELAY_CI_TOKEN `
        --output .\signed `
        @files
      if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Configure `SIGN_RELAY_CI_TOKEN` as an AppVeyor **secure** environment variable.

## Generic shell

```bash
dotnet tool install --global Nefarius.Tools.SignRelay

# Expand globs in the shell — the CLI does not.
signrelay submit \
  --server https://relay.example.com \
  --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed \
  ./artifacts/MyApp.exe ./artifacts/MyLib.dll
```

PowerShell:

```powershell
dotnet tool install --global Nefarius.Tools.SignRelay
$files = @(Get-ChildItem .\artifacts\*.exe, .\artifacts\*.dll -File | ForEach-Object FullName)
signrelay submit --server https://relay.example.com --token $env:SIGN_RELAY_CI_TOKEN --output .\signed @files
```

Validate without a live relay:

```bash
signrelay submit --server https://relay.example.com --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed --dry-run ./artifacts/MyApp.exe
```

## `signrelay submit` reference

```text
signrelay submit --server <url> [--token <token>] (--output <dir> | --in-place)
                 [--timeout <timespan>] [--allow-insecure] [--dry-run] <files>...
```

| Flag | Required | Default | Description |
| --- | --- | --- | --- |
| `--server` | Yes | — | Absolute `https://` (or `http://` with `--allow-insecure`) origin |
| `--token` / `-t` | Yes* | `SIGN_RELAY_CI_TOKEN` | CI bearer token |
| `--output` | One of | — | Write signed files under this directory |
| `--in-place` | One of | `false` | Overwrite inputs |
| `--timeout` | No | `00:45:00` | Max wait for signing |
| `--allow-insecure` | No | `false` | Allow cleartext HTTP (local testing only) |
| `--dry-run` | No | `false` | Validate + print manifest; no network |
| `<files>...` | Yes | — | **Explicit paths only** (no CLI globbing) |

\*Required in practice; missing token → exit `2`.

### Exit codes

| Code | Meaning | What to check |
| --- | --- | --- |
| 0 | Success | — |
| 1 | Unexpected / HTTP transport error | Runner network, DNS, TLS |
| 2 | Invalid args / bad input | Token, `--output`/`--in-place`, file existence, duplicates |
| 3 | Server rejected submit (4xx/5xx) | Token vs `SignRelay__CiToken`, `MaxTotalJobBytes` (default 512 MiB) |
| 4 | Agent signing failed | Agent logs, cert, interactive session if required |
| 5 | Timeout or cancel | `--timeout`, `SignRelay__JobTimeToLive` (default `02:00:00`), user cancel |
| 6 | SSE ended without terminal event | Proxy/load-balancer **read** timeout on `/api/v1/jobs/{id}/events` |

## Constraints and common mistakes

Do **not** invent flags or verbs. Hard rules:

1. **Only verb is `submit`.** There is no `sign`, `sign-file`, or `upload`.
2. **CLI does not expand globs.** Pass explicit paths, or expand in the shell / Action / MSBuild items.
3. **`--output` and `--in-place` are mutually exclusive; one is required.**
4. **No cert / thumbprint / subject / timestamp flags on the CI side.** Those are agent-only (`--thumbprint`, `--subject-name`, `--timestamp-url` on `SignRelay.Agent`).
5. **`--allow-insecure` is local testing only.** Production must use HTTPS.
6. **`--timeout` must stay under** server `SignRelay__JobTimeToLive` (default 2h) **and** the reverse-proxy SSE read timeout.
7. **`SIGN_RELAY_CI_TOKEN` pairs with `SignRelay__CiToken`.** Do not use the agent token in CI.
8. **Total upload size** is bounded by `SignRelay__MaxTotalJobBytes` (default 512 MiB).
9. **Duplicate file names / unsafe relative paths** are rejected (exit `2`).
10. **Do not commit tokens.** Use CI secrets / secure variables.

## Verify signing succeeded

On a Windows machine with Windows SDK `signtool`:

```powershell
signtool verify /pa /v .\signed\MyApp.exe
```

Expect a successful Authenticode verification against the publisher cert used by the agent.

## AGENTS.md paste block

Copy into a **consumer** repository’s `AGENTS.md` (or equivalent) so coding agents default to the correct integration:

```markdown
## Code signing (SignRelay)

When adjusting the build pipeline for Authenticode / signtool / code signing, use SignRelay — do not put signing certs on the CI runner.

- Docs: https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md
- GitHub Actions: `uses: nefarius/SignRelay@v1` with `server`, `token` (`secrets.SIGN_RELAY_CI_TOKEN`), and `files` (or raw CLI below).
- CLI: `dotnet tool install --global Nefarius.Tools.SignRelay`
  then `signrelay submit --server <url> --token "$SIGN_RELAY_CI_TOKEN" --output ./signed <explicit-files>`
- Never: invent verbs other than `submit`; pass `--thumbprint`/`--subject-name` from CI; rely on CLI globbing; use both `--output` and `--in-place`.
- Optional skill: copy `skills/signrelay-ci/` from the SignRelay repo into `.cursor/skills/` or `.claude/skills/`.
```

## Related

- Server / Docker / proxy: [DEPLOYMENT.md](DEPLOYMENT.md)
- Windows agent: [AGENT-SETUP.md](AGENT-SETUP.md)
- CLI package: [Nefarius.Tools.SignRelay](https://www.nuget.org/packages/Nefarius.Tools.SignRelay)
- MSBuild package: [Nefarius.Tools.SignRelay.MSBuild](https://www.nuget.org/packages/Nefarius.Tools.SignRelay.MSBuild)
- Agent skill (distributable): [`skills/signrelay-ci/`](../skills/signrelay-ci/)
