# SignRelay deployment notes

## Build prerequisites

- **.NET SDK 10.x** — the repo pins a minimum SDK in [global.json](../global.json) (`rollForward: latestFeature`); install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or a newer 10.x patch.
- **NUKE** — automation lives under [build/](../build/). You do **not** need the global `nuke` tool: use the bootstrappers at the repo root.

### Versioning ([MinVer](https://github.com/adamralph/minver))

- Shipping projects use **MinVer** with tag prefix **`v`** (e.g. tag **`v1.2.3`** in Git). Version flows into assemblies, NuGet packages, and published outputs.
- The NUKE project [`build/_build.csproj`](../build/_build.csproj) is excluded from MinVer.
- **Docker** builds do **not** copy **`.git`** into the image. The image build receives the version via **`MINVERVERSIONOVERRIDE`** (computed on the host). NUKE **`DockerServer`** runs `dotnet msbuild … -getProperty:Version` at the repo root and passes that value as a Docker build arg. Override with **`--MinVerVersionOverride 1.2.3`** when you need an explicit version without Git.

### NUKE targets (pack & publish)

Outputs go under **`artifacts/`** (gitignored): NuGet packages in **`artifacts/packages`**, published apps in **`artifacts/publish/<ProjectName>`**, release zips in **`artifacts/release`**.

| Target | What it does |
|--------|----------------|
| `PackContracts` | `dotnet pack` [SignRelay.Contracts](../src/SignRelay.Contracts/SignRelay.Contracts.csproj) |
| `PackCli` | `dotnet pack` [SignRelay.Cli](../src/SignRelay.Cli/SignRelay.Cli.csproj) (global tool package) |
| `PublishServer` | `dotnet publish` [SignRelay.Server](../src/SignRelay.Server/SignRelay.Server.csproj) |
| `PublishAgent` | Self-contained **`win-x64`** publish of [SignRelay.Agent](../src/SignRelay.Agent/SignRelay.Agent.csproj) (no .NET runtime required on the signing machine) |
| `Publish` | All of the above (default entry target for `dotnet run --project build/...`) |
| `All` | Same as `Publish` |
| `Release` | `PublishAgent` + `PublishServer`, then zip + SHA-256 `checksums.txt` under `artifacts/release/` |

**Container image (optional, separate command)** — requires Docker or Podman on `PATH`:

| Target | What it does |
|--------|----------------|
| `DockerServer` | Builds [docker/Dockerfile](../docker/Dockerfile) with context at the **repository root** (same as [compose.yml](../docker/compose.yml)). Injects **`MINVERVERSIONOVERRIDE`** from host MinVer/`dotnet msbuild` (no `.git` in context). Tags **`signrelay/server:latest`** by default — use **`--ServerDockerImage`**. Optional **`--MinVerVersionOverride`** for CI. The engine is **auto-detected** (`docker` first, then `podman`); override with **`--ContainerEngine podman`**. Pass **`--PushImage`** to push the tagged image after a successful build (authenticate with the registry beforehand). |

Examples:

```powershell
# Windows — builds the NUKE project, then runs the target
.\build.ps1 Publish
.\build.ps1 PackCli
.\build.ps1 PublishServer
.\build.ps1 PublishAgent
.\build.ps1 Release
.\build.ps1 DockerServer
.\build.ps1 DockerServer --ServerDockerImage myregistry/signrelay:1.0
.\build.ps1 DockerServer --MinVerVersionOverride 2.0.0
# Explicit Podman (also works without --ContainerEngine if only Podman is on PATH)
.\build.ps1 DockerServer --ContainerEngine podman
# Build and push (authenticate with the registry first)
.\build.ps1 DockerServer --ServerDockerImage myregistry/signrelay:1.0 --PushImage
```

Manual **`docker build`** / **`podman build`** / **`docker compose build`** (without NUKE): set **`MINVERVERSIONOVERRIDE`** to the same value MinVer would compute (requires a clone with Git tags), then build:

```powershell
$v = dotnet msbuild src/SignRelay.Server/SignRelay.Server.csproj -restore -getProperty:Version -nologo -verbosity:quiet
# Docker
docker build -f docker/Dockerfile --build-arg MINVERVERSIONOVERRIDE=$v -t signrelay/server:latest .
# Podman (same arguments)
podman build -f docker/Dockerfile --build-arg MINVERVERSIONOVERRIDE=$v -t signrelay/server:latest .
```

For **`docker compose`** / **`podman compose`**, export **`MINVERVERSIONOVERRIDE`** before building (see [compose.yml](../docker/compose.yml) `build.args`), or rely on NUKE **`DockerServer`** which passes the arg automatically.

```bash
# Linux / macOS
chmod +x ./build.sh
./build.sh Publish
```

Equivalent without the scripts:

```bash
dotnet run --project build/_build.csproj -- Publish
dotnet run --project build/_build.csproj -- DockerServer
dotnet run --project build/_build.csproj -- Release
```

## Relay server (Docker / Podman / VPS)

- Map **port 8080** (or place a reverse proxy in front with TLS). The container listens on `0.0.0.0:8080`.
- Persist **`SignRelay__StoragePath`** (default `/data` in [compose.yml](../docker/compose.yml)) so SQLite and uploaded blobs survive restarts.
- Set secrets via environment variables (ASP.NET Core configuration):
  - **`SignRelay__CiToken`** — bearer token used by CI / `signrelay submit --token`.
  - **`SignRelay__AgentToken`** — bearer token used by the Windows agent for lease, upload, and complete.
- **Podman rootless note**: the container runs as UID 1001. With rootless Podman and a bind-mounted host directory, ensure the host path is owned by or accessible to the subuid-mapped user (`podman unshare chown 1001:1001 ./data`). Named volumes (as in [compose.yml](../docker/compose.yml)) are managed by Podman and do not require manual ownership changes.
- **Job size and lifetime** — tune in [appsettings.json](../src/SignRelay.Server/appsettings.json) or environment:
  - `SignRelay__MaxTotalJobBytes` (default 512 MiB). Kestrel **and** multipart form limits are both set to `MaxTotalJobBytes × 2` so large uploads are not capped by ASP.NET Core’s 128 MiB multipart default.
  - `SignRelay__JobTimeToLive` (TimeSpan, e.g. `02:00:00`)
  - `SignRelay__ArtifactCleanupDelay` — grace period after terminal state before on-disk artifacts are deleted (default `01:00:00`)
  - `SignRelay__JobRecordRetention` — how long terminal **SQLite rows** are kept after completion (default `7.00:00:00`). Must be ≥ `ArtifactCleanupDelay`.

### Published container image

Tagged releases push to **Azure Container Registry**:

| Tag | Image |
| --- | --- |
| Version | `nefarius.azurecr.io/signrelay:<version>` (e.g. `1.2.3` from tag `v1.2.3`) |
| Latest | `nefarius.azurecr.io/signrelay:latest` |

Production compose example: [docker-compose.prod.yml](../docker-compose.prod.yml) (`docker login nefarius.azurecr.io` may be required to pull).

**Local compose** ([docker/compose.yml](../docker/compose.yml)) sets `ASPNETCORE_ENVIRONMENT=Development` and is for **local development only** — token validation is relaxed and Swagger is enabled. Do **not** use it in production; use `docker-compose.prod.yml` or set `ASPNETCORE_ENVIRONMENT=Production` with strong tokens (≥ 32 characters).

### Reverse proxy long reads (SSE)

The CI client holds **`GET /api/v1/jobs/{id}/events`** open for the whole signing window (Server-Sent Events). Configure your proxy so the **read timeout** is at least as large as the longest expected job (often 30–60 minutes):

- **nginx**: `proxy_read_timeout 3600s;` (and similar for `send_timeout` if needed) on the location that fronts the relay.
- **Caddy**: `flush_interval -1` and appropriate timeouts on the route.
- **Traefik**: increase forwarding timeouts for the service.

Without this, the proxy may close the stream while the desktop is still signing, and the CI step fails.

## Windows agent

**Operator walkthrough (recommended):** [AGENT-SETUP.md](AGENT-SETUP.md) — download release zip, `install` / `status` / `uninstall`, paths, upgrade, troubleshooting.

### Quick install (self-contained release)

```powershell
# From an elevated console in the extracted agent directory:
.\SignRelay.Agent.exe install `
  --relay-url https://relay.example.com `
  --token "<agent-token>" `
  --thumbprint "<sha1-thumbprint>" `
  --start

.\SignRelay.Agent.exe status
```

Verbs: **`install`**, **`uninstall`** (`--purge` removes `%ProgramData%\SignRelay\Agent`), **`status`**.

### Configuration

Machine settings written by `install`:

`%ProgramData%\SignRelay\Agent\agent.settings.json`

**Precedence** (highest first): environment variables → machine settings file → `appsettings.json` beside the exe → defaults.

| Key | Purpose |
| --- | --- |
| `SignRelayAgent__RelayUrl` | Public base URL of the relay (HTTPS in production) |
| `SignRelayAgent__AgentToken` | Must match `SignRelay__AgentToken` on the server |
| `SignRelayAgent__SignToolPath` | Optional full path to `signtool.exe`. If unset/missing: PATH, then [wdkwhere](https://github.com/nefarius/wdkwhere) |
| `SignRelayAgent__CertificateThumbprint` | SHA1 thumbprint of the signing cert |
| `SignRelayAgent__TimestampServerUrl` | RFC 3161 timestamp URL |
| `SignRelayAgent__SigningExecution` | `Auto` (default), `SameProcess`, or `InteractiveUser` |
| `SignRelayAgent__JobStagingRoot` | Optional. Interactive staging root (default `%ProgramData%\SignRelay\Agent\jobs`) |
| `SignRelayAgent__LoadUserProfileForInteractiveSigning` | Default `true` — load user profile for Current User cert stores |

**Signing modes:** In **`Auto`**, when the agent runs as a **Windows Service**, `signtool` is launched in the **active console user session** so smart-card prompts, CSP UI, and user certificate stores work. When run from a console (development), signing stays in-process. Use **`InteractiveUser`** to force interactive-session signing, or **`SameProcess`** to force in-process signing even under the service.

**Observability:** rolling logs under `%ProgramData%\SignRelay\Agent\logs\`; Windows Event Log source **SignRelay Agent**.

### Service account and session notes

- **Account**: **`LocalSystem`** so the service can obtain the active console session user token (`WTSQueryUserToken`). A virtual or network service account typically **cannot** launch processes in the interactive session this way.
- **No logged-on console user**: If nobody is logged on at the physical console, interactive signing cannot start `signtool` in a user session; jobs will fail until a user is logged on.
- **RDP / multiple sessions**: The implementation targets the **active console session** (`WTSGetActiveConsoleSessionId`). Remote-only or multi-user scenarios may need a different session selection in a future version.

### Manual / from-source publish

```powershell
.\build.ps1 PublishAgent
# Output: artifacts/publish/SignRelay.Agent\ (self-contained win-x64)
cd artifacts\publish\SignRelay.Agent
.\SignRelay.Agent.exe install --relay-url ... --token ... --start
```

## CI usage

Install the CLI as a global tool from NuGet:

```bash
dotnet tool install --global Nefarius.Tools.SignRelay
signrelay submit --server https://relay.example.com --token "$SIGN_RELAY_CI_TOKEN" --output ./signed ./artifacts/MyApp.exe
```

Set **`SIGN_RELAY_CI_TOKEN`** to the same value as **`SignRelay__CiToken`** on the server.

Tagged releases (`v*`) also publish:

- NuGet package **Nefarius.Tools.SignRelay**
- GitHub Release assets: agent zip, server zip, `checksums.txt`
- Container image `nefarius.azurecr.io/signrelay:<version>` and `:latest`

## Security checklist

- Use **TLS** in front of the relay; do not expose plaintext tokens on untrusted networks.
- Rotate **CI** and **agent** tokens independently; they are distinct principals.
- Restrict who can reach the relay API (firewall / VPN) if possible.
- Keep agent machine settings under `%ProgramData%` (ACL-restricted); do not commit tokens.
- Prefer the self-contained agent release over copying framework-dependent builds to signing machines.
