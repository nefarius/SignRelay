# SignRelay deployment notes

## Build prerequisites

- **.NET SDK 10.x** — the repo pins a minimum SDK in [global.json](../global.json) (`rollForward: latestFeature`); install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or a newer 10.x patch.
- **NUKE** — automation lives under [build/](../build/). You do **not** need the global `nuke` tool: use the bootstrappers at the repo root.

### Versioning ([MinVer](https://github.com/adamralph/minver))

- Shipping projects use **MinVer** with tag prefix **`v`** (e.g. tag **`v1.2.3`** in Git). Version flows into assemblies, NuGet packages, and published outputs.
- The NUKE project [`build/_build.csproj`](../build/_build.csproj) is excluded from MinVer.
- **Docker** builds do **not** copy **`.git`** into the image. The image build receives the version via **`MINVERVERSIONOVERRIDE`** (computed on the host). NUKE **`DockerServer`** runs `dotnet msbuild … -getProperty:Version` at the repo root and passes that value as a Docker build arg. Override with **`--MinVerVersionOverride 1.2.3`** when you need an explicit version without Git.

### NUKE targets (pack & publish)

Outputs go under **`artifacts/`** (gitignored): NuGet packages in **`artifacts/packages`**, published apps in **`artifacts/publish/<ProjectName>`**.

| Target | What it does |
|--------|----------------|
| `PackContracts` | `dotnet pack` [SignRelay.Contracts](../src/SignRelay.Contracts/SignRelay.Contracts.csproj) |
| `PackCli` | `dotnet pack` [SignRelay.Cli](../src/SignRelay.Cli/SignRelay.Cli.csproj) (global tool package) |
| `PublishServer` | `dotnet publish` [SignRelay.Server](../src/SignRelay.Server/SignRelay.Server.csproj) |
| `PublishAgent` | `dotnet publish` [SignRelay.Agent](../src/SignRelay.Agent/SignRelay.Agent.csproj) |
| `Publish` | All of the above (default entry target for `dotnet run --project build/...`) |
| `All` | Same as `Publish` |

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
```

## Relay server (Docker / Podman / VPS)

- Map **port 8080** (or place a reverse proxy in front with TLS). The container listens on `0.0.0.0:8080`.
- Persist **`SignRelay__StoragePath`** (default `/data` in [compose.yml](../docker/compose.yml)) so SQLite and uploaded blobs survive restarts.
- Set secrets via environment variables (ASP.NET Core configuration):
  - **`SignRelay__CiToken`** — bearer token used by CI / `signrelay submit --token`.
  - **`SignRelay__AgentToken`** — bearer token used by the Windows agent for lease, upload, and complete.
- **Podman rootless note**: the container runs as UID 1001. With rootless Podman and a bind-mounted host directory, ensure the host path is owned by or accessible to the subuid-mapped user (`podman unshare chown 1001:1001 ./data`). Named volumes (as in [compose.yml](../docker/compose.yml)) are managed by Podman and do not require manual ownership changes.
- **Job size and lifetime** — tune `SignRelay__MaxTotalJobBytes` and `SignRelay__JobTimeToLive` (TimeSpan format, e.g. `02:00:00`) in [appsettings.json](../src/SignRelay.Server/appsettings.json) or environment.

### Reverse proxy long reads (SSE)

The CI client holds **`GET /api/v1/jobs/{id}/events`** open for the whole signing window (Server-Sent Events). Configure your proxy so the **read timeout** is at least as large as the longest expected job (often 30–60 minutes):

- **nginx**: `proxy_read_timeout 3600s;` (and similar for `send_timeout` if needed) on the location that fronts the relay.
- **Caddy**: `flush_interval -1` and appropriate timeouts on the route.
- **Traefik**: increase forwarding timeouts for the service.

Without this, the proxy may close the stream while the desktop is still signing, and the CI step fails.

## Windows agent

- Run the agent on the machine that holds the code-signing certificate. Configure [appsettings.json](../src/SignRelay.Agent/appsettings.json) or user-secrets / environment:
  - **`SignRelayAgent__RelayUrl`** — public base URL of the relay (HTTPS in production).
  - **`SignRelayAgent__AgentToken`** — must match **`SignRelay__AgentToken`** on the server.
  - **`SignRelayAgent__SignToolPath`** — full path to `signtool.exe` from the Windows SDK. If that file does not exist and `signtool.exe` is not on `PATH`, the agent falls back to **[wdkwhere](https://github.com/nefarius/wdkwhere)** (`wdkwhere run signtool …`), which you can install with `dotnet tool install --global Nefarius.Tools.WDKWhere` ([NuGet](https://www.nuget.org/packages/Nefarius.Tools.WDKWhere)).
  - **`SignRelayAgent__CertificateThumbprint`** — SHA1 thumbprint of the signing cert (if required by your signing workflow).
  - **`SignRelayAgent__SigningExecution`** — `Auto` (default), `SameProcess`, or `InteractiveUser`. In **`Auto`**, when the agent runs as a **Windows Service**, `signtool` is launched in the **active console user session** so smart-card prompts, CSP UI, and user certificate stores work. When run from a console (development), signing stays in-process. Use **`InteractiveUser`** to force interactive-session signing if detection misbehaves, or **`SameProcess`** to force in-process signing even under the service.
  - **`SignRelayAgent__JobStagingRoot`** — optional. When interactive signing is used, job files are staged under this directory (default: **`%ProgramData%\SignRelay\Agent\jobs`**). The service grants the console user access to each job folder. For non-interactive (console) runs, staging remains under `%TEMP%\signrelay\<jobId>`.
  - **`SignRelayAgent__LoadUserProfileForInteractiveSigning`** — when `true` (default), the user profile is loaded for interactive `signtool` so **Current User** certificate stores resolve correctly.

### Windows Service installation

- Publish the agent (`PublishAgent` / `dotnet publish` on [SignRelay.Agent](../src/SignRelay.Agent/SignRelay.Agent.csproj)), then register a service that runs **`SignRelay.Agent.exe`** (adjust paths to your publish folder):

```powershell
sc.exe create SignRelayAgent binPath= "C:\Path\To\publish\SignRelay.Agent.exe" start= auto obj= LocalSystem
sc.exe description SignRelayAgent "SignRelay signing agent"
sc.exe start SignRelayAgent
```

- **Account**: Use **`LocalSystem`** (the default for `obj= LocalSystem` above) so the service can obtain the active console session user token (`WTSQueryUserToken`). A virtual or network service account typically **cannot** launch processes in the interactive session this way.
- **No logged-on console user**: If nobody is logged on at the physical console, interactive signing cannot start `signtool` in a user session; jobs will fail until a user is logged on.
- **RDP / multiple sessions**: The implementation targets the **active console session** (`WTSGetActiveConsoleSessionId`). Remote-only or multi-user scenarios may need a different session selection in a future version.
- **Remove** the service:

```powershell
sc.exe stop SignRelayAgent
sc.exe delete SignRelayAgent
```

- **Interactive session**: When **`SigningExecution`** is **`Auto`** and the process is a Windows Service, `signtool` runs in the logged-on console user’s session, so smart card or password UI from your CSP should appear on that desktop.

## CI usage

Install the CLI as a global tool from the packaged NuGet, or invoke the published `SignRelay.Cli` binary. Example:

```bash
signrelay submit --server https://relay.example.com --token "$SIGN_RELAY_CI_TOKEN" --output ./signed ./artifacts/MyApp.exe
```

Set **`SIGN_RELAY_CI_TOKEN`** to the same value as **`SignRelay__CiToken`** on the server.

## Security checklist

- Use **TLS** in front of the relay; do not expose plaintext tokens on untrusted networks.
- Rotate **CI** and **agent** tokens independently; they are distinct principals.
- Restrict who can reach the relay API (firewall / VPN) if possible.
