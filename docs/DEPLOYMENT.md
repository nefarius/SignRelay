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

**Docker image (optional, separate command)** — requires Docker on `PATH`:

| Target | What it does |
|--------|----------------|
| `DockerServer` | `docker build` using [docker/Dockerfile](../docker/Dockerfile) with build context at the **repository root** (same as [compose.yml](../docker/compose.yml)). Injects **`MINVERVERSIONOVERRIDE`** from host MinVer/`dotnet msbuild` (no `.git` in context). Tags **`signrelay/server:latest`** by default — use **`--ServerDockerImage`**. Optional **`--MinVerVersionOverride`** for CI. |

Examples:

```powershell
# Windows — builds the NUKE project, then runs the target
.\build.ps1 Publish
.\build.ps1 PackCli
.\build.ps1 PublishServer
.\build.ps1 DockerServer
.\build.ps1 DockerServer --ServerDockerImage myregistry/signrelay:1.0
.\build.ps1 DockerServer --MinVerVersionOverride 2.0.0
```

Manual **`docker build`** / **`docker compose build`** (without NUKE): set **`MINVERVERSIONOVERRIDE`** to the same value MinVer would compute (requires a clone with Git tags), then build:

```powershell
$v = dotnet msbuild src/SignRelay.Server/SignRelay.Server.csproj -restore -getProperty:Version -nologo -verbosity:quiet
docker build -f docker/Dockerfile --build-arg MINVERVERSIONOVERRIDE=$v -t signrelay/server:latest .
```

For **`docker compose`**, export **`MINVERVERSIONOVERRIDE`** before building (see [compose.yml](../docker/compose.yml) `build.args`), or rely on NUKE **`DockerServer`** which passes the arg automatically.

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

## Relay server (Docker / VPS)

- Map **port 8080** (or place a reverse proxy in front with TLS). The container listens on `0.0.0.0:8080`.
- Persist **`SignRelay__StoragePath`** (default `/data` in [compose.yml](../docker/compose.yml)) so SQLite and uploaded blobs survive restarts.
- Set secrets via environment variables (ASP.NET Core configuration):
  - **`SignRelay__CiToken`** — bearer token used by CI / `signrelay submit --token`.
  - **`SignRelay__AgentToken`** — bearer token used by the Windows agent for lease, upload, and complete.
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
- **Interactive session**: If unlocking the key store requires UI (smart card or password UI), the process must run in an **interactive user session** (e.g. logon startup task or tray host). A session-0 Windows Service alone may not see prompts from your existing unlock software.

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
