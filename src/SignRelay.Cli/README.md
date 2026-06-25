# Nefarius.Tools.SignRelay

.NET global tool — submit files to a [SignRelay](https://github.com/nefarius/SignRelay) server, wait for signing (Server-Sent Events), and download the signed outputs. Keeps code-signing keys off the CI runner entirely.

## Install

```bash
dotnet tool install --global Nefarius.Tools.SignRelay
```

Command name: `signrelay`

## Requirements

- .NET 10 runtime (or SDK)
- A running SignRelay server and a valid CI token (`SignRelay__CiToken`)

## Quick start

```bash
signrelay submit \
  --server https://relay.example.com \
  --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed \
  ./artifacts/MyApp.exe
```

## Usage

```
signrelay submit --server <url> [--token <token>] (--output <dir> | --in-place) [options] <files>...
```

### Options

- `--server <url>` **(required)** — Base URL of the SignRelay server, e.g. `https://relay.example.com`.
- `--token <token>` / `-t <token>` — CI bearer token. Falls back to the `SIGN_RELAY_CI_TOKEN` environment variable.
- `--output <dir>` — Write signed files under this directory, preserving relative paths.
- `--in-place` — Overwrite each input file with its signed copy. Mutually exclusive with `--output`.
- `--timeout <timespan>` — Maximum time to wait for signing to complete. Default: `00:45:00` (45 minutes).
- `--allow-insecure` — Allow `http://` server URLs. Not recommended; bearer tokens are sent in cleartext.

Either `--output` or `--in-place` must be specified, but not both.

### Arguments

- `<files>...` **(required)** — One or more paths to the files to sign.

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

## Supported systems

- Any OS with a .NET 10 runtime.
- The **signing agent** (separate component) is Windows-only. This CLI runs wherever .NET 10 runs.

## Server and agent setup

See the [SignRelay repository](https://github.com/nefarius/SignRelay) for server deployment, agent configuration, and reverse-proxy setup.

## License

MIT — Copyright (c) 2026 Benjamin Höglinger-Stelzer.
