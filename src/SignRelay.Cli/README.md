# Nefarius.Tools.SignRelay

[![NuGet](https://img.shields.io/nuget/v/Nefarius.Tools.SignRelay.svg)](https://www.nuget.org/packages/Nefarius.Tools.SignRelay)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/nefarius/SignRelay/blob/master/LICENSE)

.NET global tool — submit files to a [SignRelay](https://github.com/nefarius/SignRelay) server, wait for signing (Server-Sent Events), and download the signed outputs. Keeps code-signing keys off the CI runner entirely.

## About

`signrelay` is the CI-facing client for SignRelay. It uploads one or more files to the relay, waits on the job’s SSE stream until the Windows agent finishes `signtool`, then downloads the signed artifacts (`--output` or `--in-place`).

## Features

- Single verb: **`submit`**
- Bearer auth via `--token` or `SIGN_RELAY_CI_TOKEN`
- SSE wait with configurable `--timeout` (default 45 minutes)
- `--dry-run` — validate args and print the job manifest without contacting the server
- Stable exit codes `0`–`6` for CI scripting

## Supported systems

| Component | OS | Architecture | Runtime |
| --- | --- | --- | --- |
| **CLI (this tool)** | Windows, Linux, macOS | x64, Arm64 | [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) runtime or SDK |
| **Signing agent** (separate) | Windows 10 / 11 only | x64 (primary) | Self-contained release zip; not required on the CI runner |

Unsupported: using this tool as a substitute for local `signtool` without a running SignRelay server and online agent.

## Install

```bash
dotnet tool install --global Nefarius.Tools.SignRelay --version 1.0.0
```

Omit `--version` only when you intentionally want the latest NuGet version. Prefer pinning. Command name: `signrelay`.

Requires a running SignRelay server (same major release as this tool; see [releases](https://github.com/nefarius/SignRelay/releases)) and a CI token matching `SignRelay__CiToken`.

## Quick start

```bash
signrelay submit \
  --server https://relay.example.com \
  --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed \
  ./artifacts/MyApp.exe
```

## Usage

```text
signrelay submit --server <url> [--token <token>] (--output <dir> | --in-place) [options] <files>...
```

### Options

- `--server <url>` **(required)** — Base URL of the SignRelay server, e.g. `https://relay.example.com`.
- `--token <token>` / `-t <token>` — CI bearer token. Falls back to the `SIGN_RELAY_CI_TOKEN` environment variable.
- `--output <dir>` — Write signed files under this directory, preserving relative paths.
- `--in-place` — Overwrite each input file with its signed copy. Mutually exclusive with `--output`.
- `--timeout <timespan>` — Maximum time to wait for signing to complete. Default: `00:45:00` (45 minutes).
- `--allow-insecure` — Allow `http://` server URLs. Not recommended; bearer tokens are sent in cleartext.
- `--dry-run` — Validate arguments and resolve input files, print the job manifest, then exit without contacting the server.

Either `--output` or `--in-place` must be specified, but not both.

### Arguments

- `<files>...` **(required)** — One or more **explicit** paths to the files to sign. The CLI does not expand globs.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Unexpected error |
| 2 | Invalid arguments or bad input |
| 3 | Server rejected the submit (4xx/5xx) |
| 4 | Signing failed on the agent side |
| 5 | Timeout or connection lost before signing completed |
| 6 | SSE stream ended without a terminal event (server or proxy issue) |

## Environment variables

- `SIGN_RELAY_CI_TOKEN` — CI bearer token. Used when `--token` is not passed.

## Build prerequisites (contributors)

| Tool | Version |
| --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | **10.0.100** minimum (`rollForward: latestFeature` in repo [`global.json`](https://github.com/nefarius/SignRelay/blob/master/global.json)) |
| Git | **2.40+** recommended (MinVer tags use `v` prefix) |

```bash
git clone https://github.com/nefarius/SignRelay.git
cd SignRelay
dotnet restore SignRelay.sln
dotnet pack src/SignRelay.Cli/SignRelay.Cli.csproj -c Release -o ./artifacts/nuget
```

Or via NUKE: `./build.sh PackCli` / `.\build.ps1 PackCli`.

## Support policy

- Use the [SignRelay issue tracker](https://github.com/nefarius/SignRelay/issues) for defects in this tool.
- Operational setup (relay URL, tokens, proxies, certificates) is out of scope for the issue tracker — read [CI-INTEGRATION.md](https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md) and [DEPLOYMENT.md](https://github.com/nefarius/SignRelay/blob/master/docs/DEPLOYMENT.md) first.
- Issues without reproduction details may be closed.

## Server and agent setup

- Repository: [nefarius/SignRelay](https://github.com/nefarius/SignRelay)
- CI integration: [docs/CI-INTEGRATION.md](https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md)
- Agent install: [docs/AGENT-SETUP.md](https://github.com/nefarius/SignRelay/blob/master/docs/AGENT-SETUP.md)
- Deployment: [docs/DEPLOYMENT.md](https://github.com/nefarius/SignRelay/blob/master/docs/DEPLOYMENT.md)

## License

MIT — Copyright (c) 2026 Benjamin Höglinger-Stelzer.

## Legal / trademark notes

**Windows**, **.NET**, and other product names are trademarks of their respective owners. References here are for identification only.

## Sources / credits

- Project: [nefarius/SignRelay](https://github.com/nefarius/SignRelay)
- CLI parsing: [System.CommandLine](https://www.nuget.org/packages/System.CommandLine)
- Versioning: [MinVer](https://github.com/adamralph/minver)
