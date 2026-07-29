# Nefarius.Tools.SignRelay.MSBuild

MSBuild targets that call [`signrelay submit`](https://www.nuget.org/packages/Nefarius.Tools.SignRelay) after `Publish` (opt-in). Keeps code-signing keys off the CI runner.

## Prerequisites

- .NET SDK that can restore packages
- Global tool **Nefarius.Tools.SignRelay** on `PATH` (or set `SignRelayToolPath`)
- Reachable SignRelay server and CI token

## Install

```xml
<ItemGroup>
  <PackageReference Include="Nefarius.Tools.SignRelay.MSBuild" Version="*" PrivateAssets="all" />
</ItemGroup>
```

## Enable (CI only)

Signing is **off by default** so local builds never hit the network.

```bash
dotnet publish -c Release \
  /p:SignRelayEnabled=true \
  /p:SignRelayServer=https://relay.example.com
```

Set `SIGN_RELAY_CI_TOKEN` (or `/p:SignRelayToken=...`) to the same value as `SignRelay__CiToken` on the server.

## Properties

| Property | Default | Description |
| --- | --- | --- |
| `SignRelayEnabled` | `false` | Master switch |
| `SignRelayServer` | _(required when enabled)_ | Relay base URL |
| `SignRelayToken` | `$(SIGN_RELAY_CI_TOKEN)` | CI bearer token |
| `SignRelayTimeout` | `00:45:00` | Passed to `--timeout` |
| `SignRelayToolPath` | _(resolve from PATH / `~/.dotnet/tools`)_ | Absolute path to `signrelay` |
| `SignRelaySignTargetPath` | `true` | When no `SignRelayFile` items, sign `$(TargetPath)` / publish output |
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

## Docs

- [CI integration](https://github.com/nefarius/SignRelay/blob/master/docs/CI-INTEGRATION.md)
- [Deployment](https://github.com/nefarius/SignRelay/blob/master/docs/DEPLOYMENT.md)

## License

MIT — Copyright (c) 2026 Benjamin Höglinger-Stelzer.
