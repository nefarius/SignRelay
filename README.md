# SignRelay

[![Build](https://github.com/nefarius/SignRelay/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/nefarius/SignRelay/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

CI submits files to a small relay; a Windows agent runs `signtool` with your certificate and returns signed artifacts—without putting the signing key on the CI runner.

## About

SignRelay is three parts: an **ASP.NET Core** relay ([`SignRelay.Server`](src/SignRelay.Server/)) that stores jobs and streams progress (Server-Sent Events), a **Windows agent** ([`SignRelay.Agent`](src/SignRelay.Agent/)) that leases work and signs with the Windows SDK’s `signtool`, and a **CLI** ([`SignRelay.Cli`](src/SignRelay.Cli/)) for pipelines to submit files and wait for signed outputs.

Operational detail—Docker, reverse proxies and SSE timeouts, MinVer, and NUKE targets—is in **[`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md)**. Windows agent install: **[`docs/AGENT-SETUP.md`](docs/AGENT-SETUP.md)**.

## Features

- Bearer-token auth with separate **CI** and **agent** roles.
- Job lifecycle with SQLite metadata and blob storage under a configurable data directory.
- **SSE** endpoint for CI to wait on signing completion (`GET /api/v1/jobs/{id}/events`).
- Agent **signing execution modes** (`Auto`, `SameProcess`, `InteractiveUser`) for console vs Windows Service and interactive certificate/smart-card UI.
- Agent **self-install verbs** (`install` / `uninstall` / `status`) with machine config under `%ProgramData%`, Event Log + rolling file logs, and self-contained **win-x64** release zips.
- CLI **`signrelay submit`** with `--token` or `SIGN_RELAY_CI_TOKEN`, optional `--output` or `--in-place`, and configurable `--timeout`.
- Optional **Docker or Podman** image build for the server (see deployment doc).

## Limitations / scope boundaries

- The **signing agent targets Windows** (certificate store, `signtool`, session behavior). Other platforms are not supported for the agent.
- Signing assumes a **Microsoft-style** `signtool` workflow; other toolchains are out of scope.
- Interactive signing relies on an **appropriate logged-on session** where applicable; multi-session/RDP edge cases are documented in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).
- A **reverse proxy** in front of the relay must allow **long-lived reads** for the SSE stream (see deployment doc).
- **Container image** builds (Docker or Podman) do not copy `.git`; image version uses **`MINVERVERSIONOVERRIDE`** (NUKE `DockerServer` sets this). See [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).

## Supported systems / environment

| Component | Supported |
| --- | --- |
| **SDK (build)** | [.NET SDK 10.x](https://dotnet.microsoft.com/download/dotnet/10.0), minimum **10.0.100** per [`global.json`](global.json) (`rollForward: latestFeature`) |
| **Relay server** | **Linux x64** for container/VPS-style deployment (default CI is `ubuntu-latest`; server is ASP.NET Core + SQLite) |
| **Agent** | **Windows 10/11**, **x64** (primary); uses Windows SDK `signtool` (see agent options in deployment doc) |
| **CLI** | .NET 10–compatible runtime (same SDK builds the tool; install as global tool from packaged NuGet per deployment doc) |
| **Git** | **2.40+** recommended (tags use **`v`** prefix for MinVer, e.g. `v1.2.3`) |

## Quick start (developers)

1. Clone the repository.
2. Restore and build:

   ```bash
   dotnet restore SignRelay.sln
   dotnet build SignRelay.sln -c Release
   ```

3. Or run the NUKE **Publish** pipeline (artifacts under `artifacts/`, gitignored):

   ```bash
   chmod +x ./build.sh
   ./build.sh Publish
   ```

   On Windows: `.\build.ps1 Publish`

See [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) for target names (`PackContracts`, `PackCli`, `PublishServer`, `PublishAgent`, `DockerServer`, etc.).

## Installation / usage (operators)

**Server (production image):** pin a release tag, e.g. `nefarius.azurecr.io/signrelay:1.0.0` — see [`docker-compose.prod.yml`](docker-compose.prod.yml) (that file may track `:latest` for convenience; prefer a version tag in production). Set at least:

- `SignRelay__CiToken` — bearer token for CI / CLI (≥ 32 characters).
- `SignRelay__AgentToken` — bearer token for the agent (≥ 32 characters).
- Persist `SignRelay__StoragePath` (compose uses `/data`).

Local Docker/Podman for development only: [`docker/compose.yml`](docker/compose.yml) (`ASPNETCORE_ENVIRONMENT=Development` — not for production).

**Agent (Windows):** download `SignRelay.Agent-<version>-win-x64.zip` from [Releases](https://github.com/nefarius/SignRelay/releases), extract, then from an elevated console:

```powershell
.\SignRelay.Agent.exe install --relay-url https://relay.example.com --token "<agent-token>" --subject-name "Nefarius Software Solutions e.U." --start
.\SignRelay.Agent.exe status
```

Full walkthrough: **[`docs/AGENT-SETUP.md`](docs/AGENT-SETUP.md)**.

**CLI:**

```bash
dotnet tool install --global Nefarius.Tools.SignRelay
signrelay submit --server https://relay.example.com --token "$SIGN_RELAY_CI_TOKEN" --output ./signed ./artifacts/MyApp.exe
```

Use `SIGN_RELAY_CI_TOKEN` matching `SignRelay__CiToken` on the server.

TLS, proxy timeouts, retention, and ACR tags: **[`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md)**.

## Build prerequisites

- **.NET SDK 10.x** — pinned in [`global.json`](global.json); install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or a newer 10.x patch.
- **NUKE** — use repo bootstrappers [`build.sh`](build.sh) / [`build.ps1`](build.ps1); a global `nuke` tool is **not** required.
- **Docker or Podman** — optional, on `PATH`, for `DockerServer` / image builds. The engine is auto-detected (`docker` first, then `podman`); override with `--ContainerEngine podman`.

Versioning uses **MinVer** with tag prefix **`v`**. The build project under [`build/`](build/) is excluded from MinVer.

## Support policy

- Use the issue tracker for **defects** and **concrete improvements** to this repository.
- **Operational** or **environment-specific** setup (proxies, certificates, org policy) is your responsibility; read [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) first.
- Issues with incomplete reproduction details or requests outside documented scope may be closed.

## Security

- Terminate TLS in front of the relay in production; do not send bearer tokens over untrusted plaintext networks.
- Rotate **CI** and **agent** tokens independently; they represent different principals.
- Restrict network access to the relay where possible.

See the **Security checklist** in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).

## License

This project is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

Copyright (c) 2026 Benjamin Höglinger-Stelzer.

## Legal / trademark notes

**Windows**, **.NET**, and other product names are trademarks of their respective owners. References here are for identification only.

## Sources and credits

- Agent install walkthrough: [`docs/AGENT-SETUP.md`](docs/AGENT-SETUP.md)
- Deployment and build reference: [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md)
- Versioning: [MinVer](https://github.com/adamralph/minver)
- HTTP API: [FastEndpoints](https://fast-endpoints.com/)
- Optional `signtool` discovery: [wdkwhere](https://github.com/nefarius/wdkwhere), [Nefarius.Tools.WDKWhere](https://www.nuget.org/packages/Nefarius.Tools.WDKWhere)
- Documentation style baseline: [`.cursor/rules/readme-style.mdc`](.cursor/rules/readme-style.mdc), [`AGENTS.md`](AGENTS.md)
