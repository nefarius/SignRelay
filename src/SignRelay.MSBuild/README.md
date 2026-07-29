# Nefarius.Tools.SignRelay.MSBuild

[![NuGet](https://img.shields.io/nuget/v/Nefarius.Tools.SignRelay.MSBuild.svg)](https://www.nuget.org/packages/Nefarius.Tools.SignRelay.MSBuild)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/nefarius/SignRelay/blob/master/LICENSE)

MSBuild targets that call [`signrelay submit`](https://www.nuget.org/packages/Nefarius.Tools.SignRelay) after `Publish` (opt-in). Keeps code-signing keys off the CI runner.

## About

This package injects a `SignRelaySign` target into consuming projects. When enabled, it resolves files to sign (publish output preferred), invokes the `signrelay` global tool **in-place**, and records a stamp file so incremental builds do not re-submit.

## Features

- Opt-in via `SignRelayEnabled` (default **false** — local builds never hit the network)
- Hooks after `Publish` by default (`SignRelayAfterTargets`)
- Prefers `$(PublishDir)$(TargetFileName)`, falls back to `$(TargetPath)`
- Explicit `<SignRelayFile>` items for multi-file jobs
- Passes the CI token via `SIGN_RELAY_CI_TOKEN` (not `--token` on the process command line)
- Stamp-file incrementality under `$(IntermediateOutputPath)`

## Limitations

- Requires the **Nefarius.Tools.SignRelay** global tool on `PATH` (or `SignRelayToolPath`)
- Signs **in-place** only (no `--output` directory mode from MSBuild)
- Ordering versus `Pack` / archive targets is your responsibility — pack **after** `SignRelaySign` if archives must contain signed binaries
- Does not install or update the global tool for you

## Supported systems

| Surface | Supported |
| --- | --- |
| **Package consume** | SDK-style projects restoring NuGet packages (.NET SDK **10.0.100+** recommended; package is content-only) |
| **Host OS for MSBuild** | Windows, Linux, macOS (wherever `dotnet publish` and `signrelay` run) |
| **Signing** | Performed by a separate Windows agent; not by this package |

## Install

Pin the MSBuild package and the CLI tool to the **same** published version:

```xml
<ItemGroup>
  <PackageReference Include="Nefarius.Tools.SignRelay.MSBuild" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

```bash
dotnet tool install --global Nefarius.Tools.SignRelay --version 1.0.0
```

Check [NuGet](https://www.nuget.org/packages/Nefarius.Tools.SignRelay.MSBuild) / [releases](https://github.com/nefarius/SignRelay/releases) for the version to pin. Do not use `Version="*"`.

## Enable (CI only)

Signing is **off by default** so local builds never hit the network.

```bash
export SIGN_RELAY_CI_TOKEN='…'   # must match SignRelay__CiToken on the server
dotnet publish -c Release \
  /p:SignRelayEnabled=true \
  /p:SignRelayServer=https://relay.example.com
```

You may set `/p:SignRelayToken=...` instead of the environment variable; the targets still forward the value into `SIGN_RELAY_CI_TOKEN` for the CLI process (it is not placed on the `signrelay` command line).

## Properties

| Property | Default | Description |
| --- | --- | --- |
| `SignRelayEnabled` | `false` | Master switch |
| `SignRelayServer` | _(required when enabled)_ | Relay base URL |
| `SignRelayToken` | `$(SIGN_RELAY_CI_TOKEN)` | CI bearer token (env-forwarded to the CLI) |
| `SignRelayTimeout` | `00:45:00` | Passed to `--timeout` |
| `SignRelayToolPath` | _(resolve from PATH / `~/.dotnet/tools`)_ | Absolute path to `signrelay` |
| `SignRelaySignTargetPath` | `true` | When no `SignRelayFile` items, sign publish output then `$(TargetPath)` |
| `SignRelayAfterTargets` | `Publish` | Hook point |

## Items

```xml
<ItemGroup>
  <SignRelayFile Include="$(PublishDir)MyApp.exe" />
  <SignRelayFile Include="$(PublishDir)MyApp.dll" />
</ItemGroup>
```

## Behavior

- Signs **in-place** (`signrelay submit --in-place`).
- Uses a stamp file under `$(IntermediateOutputPath)` so incremental builds do not re-submit after the binary was already signed.
- Ordering versus `Pack` / archive targets is your responsibility — run packing **after** `SignRelaySign` if archives must contain signed binaries.

## Build prerequisites (contributors)

| Tool | Version |
| --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | **10.0.100** minimum (`rollForward: latestFeature` in repo [`global.json`](https://github.com/nefarius/SignRelay/blob/master/global.json)) |
| Git | **2.40+** recommended (MinVer tags use `v` prefix) |

```bash
git clone https://github.com/nefarius/SignRelay.git
cd SignRelay
dotnet restore SignRelay.sln
dotnet pack src/SignRelay.MSBuild/SignRelay.MSBuild.csproj -c Release -o ./artifacts/nuget
```

Or via NUKE: `./build.sh PackMsBuild` / `.\build.ps1 PackMsBuild`.

## Support policy

- Use the [SignRelay issue tracker](https://github.com/nefarius/SignRelay/issues) for defects in these targets.
- Operational setup (relay, tokens, agent, proxies) is out of scope — read [CI-INTEGRATION.md](https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md) first.
- Incomplete reproductions may be closed.

## Docs

- [CI integration](https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md)
- [Deployment](https://github.com/nefarius/SignRelay/blob/master/docs/DEPLOYMENT.md)

## License

MIT — Copyright (c) 2026 Benjamin Höglinger-Stelzer.

## Legal / trademark notes

**Windows**, **.NET**, and other product names are trademarks of their respective owners. References here are for identification only.

## Sources / credits

- Project: [nefarius/SignRelay](https://github.com/nefarius/SignRelay)
- Companion CLI: [Nefarius.Tools.SignRelay](https://www.nuget.org/packages/Nefarius.Tools.SignRelay)
- Versioning: [MinVer](https://github.com/adamralph/minver)
