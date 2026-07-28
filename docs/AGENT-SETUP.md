# SignRelay Agent setup (Windows)

Install and run the Windows signing agent as a service. The agent leases jobs from the relay, runs `signtool`, and uploads signed artifacts.

## Prerequisites

| Requirement | Notes |
| --- | --- |
| **OS** | Windows 10/11, **x64** |
| **.NET runtime** | **Not required** when using the self-contained release zip |
| **Certificate** | Code-signing cert in the store used by your workflow (typically Current User when interactive signing is enabled) |
| **signtool** | Windows SDK `signtool.exe` on `PATH`, an explicit path, **or** [wdkwhere](https://github.com/nefarius/wdkwhere) (`dotnet tool install --global Nefarius.Tools.WDKWhere`) |
| **Relay** | Reachable HTTPS URL and an agent token matching `SignRelay__AgentToken` on the server |
| **Elevation** | `install` / `uninstall` require an **Administrator** console |
| **Console user** | For interactive / smart-card signing under the service: a user must be logged on at the **physical console** |

## 1. Download

1. Open [Releases](https://github.com/nefarius/SignRelay/releases).
2. Download `SignRelay.Agent-<version>-win-x64.zip` and `checksums.txt`.
3. Verify the archive:

   ```powershell
   Get-FileHash .\SignRelay.Agent-<version>-win-x64.zip -Algorithm SHA256
   # Compare to the line in checksums.txt
   ```

4. Extract to a stable directory, e.g. `C:\Program Files\SignRelay\Agent\`.

## 2. Install

From an elevated PowerShell session in the extract directory:

```powershell
.\SignRelay.Agent.exe install `
  --relay-url https://relay.example.com `
  --token "<agent-token>" `
  --subject-name "Nefarius Software Solutions e.U." `
  --start
```

| Flag | Required | Description |
| --- | --- | --- |
| `--relay-url` | Yes | Public base URL of the relay (HTTPS in production) |
| `--token` | Yes | Must match `SignRelay__AgentToken` on the server |
| `--thumbprint` | No | Optional SHA1 thumbprint (`signtool /sha1`); may be combined with `--subject-name` |
| `--subject-name` | No | Optional certificate subject name (`signtool /n`); may be combined with `--thumbprint` |
| `--timestamp-url` | No | RFC 3161 timestamp URL (default DigiCert if left in machine config / appsettings) |
| `--signtool` | No | Full path to `signtool.exe` |
| `--signing-execution` | No | `Auto` (default), `SameProcess`, or `InteractiveUser` |
| `--agent-id` | No | Identifier reported on lease |
| `--service-name` | No | Default `SignRelayAgent` |
| `--start` | No | Start the service immediately after install |

Missing `--relay-url` / `--token` are prompted interactively when a console is attached.

What install does:

- Writes `%ProgramData%\SignRelay\Agent\agent.settings.json` (ACL: SYSTEM + Administrators only).
- Registers Event Log source **SignRelay Agent**.
- Creates a **LocalSystem**, **delayed-auto** service with restart-on-failure.
- Does **not** put the token next to the binaries (upgrade-safe).

## 3. Verify

```powershell
.\SignRelay.Agent.exe status
```

Confirms service state, config path, resolved `signtool` / wdkwhere, and probes `GET /health` on the relay.

Also check:

- **Event Viewer** → Windows Logs → Application → source **SignRelay Agent**
- Rolling logs: `%ProgramData%\SignRelay\Agent\logs\agent-*.log`

## 4. Configuration precedence

Highest wins:

1. Environment variables (`SignRelayAgent__RelayUrl`, `SignRelayAgent__AgentToken`, …)
2. `%ProgramData%\SignRelay\Agent\agent.settings.json` (written by `install`)
3. `appsettings.json` / `appsettings.{Environment}.json` next to the executable
4. Code defaults

## Paths

| Path | Purpose |
| --- | --- |
| `%ProgramData%\SignRelay\Agent\agent.settings.json` | Machine settings (token, relay URL, …) |
| `%ProgramData%\SignRelay\Agent\logs\` | Rolling Serilog files |
| `%ProgramData%\SignRelay\Agent\jobs\` | Interactive-signing job staging (default) |

## Upgrade

1. Stop the service: `sc.exe stop SignRelayAgent`
2. Replace the files in the install directory with the new zip contents (keep the same path so `binPath` stays valid).
3. Start: `sc.exe start SignRelayAgent`

Machine settings under `%ProgramData%` are left alone. Re-run `install` only when changing relay URL, token, or related options (updates config; does not recreate the service if it already exists).

## Uninstall

```powershell
.\SignRelay.Agent.exe uninstall
# Also remove machine config, logs, and staging:
.\SignRelay.Agent.exe uninstall --purge
```

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `install` exits asking for Administrator | Not elevated |
| Jobs fail until someone logs on | Interactive signing needs an **active console** session (`WTSGetActiveConsoleSessionId`) |
| RDP-only / multi-session signing fails | Unsupported today — console session only; see [DEPLOYMENT.md](DEPLOYMENT.md) |
| `signtool: NOT FOUND` in `status` | Install Windows SDK, set `--signtool`, or install wdkwhere |
| `Health: 404 Not Found` (body like `404 page not found`) | Reverse proxy has no router for the relay — often Traefik dropped an `unhealthy` container; see [DEPLOYMENT.md](DEPLOYMENT.md) (Traefik and Docker HEALTHCHECK). Not an agent config error. |
| Agent stops after 401/403 | Token mismatch with `SignRelay__AgentToken` |
| Service running but no logs | Check Event Viewer and `%ProgramData%\SignRelay\Agent\logs\` |
| Smart-card UI never appears | Ensure `SigningExecution` is `Auto` or `InteractiveUser`, service is LocalSystem, and a console user is logged on |

## Related

- Server / Docker / proxy: [DEPLOYMENT.md](DEPLOYMENT.md)
- Project overview: [README.md](../README.md)
