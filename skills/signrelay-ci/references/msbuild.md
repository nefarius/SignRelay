# MSBuild

Pin the MSBuild package and the global tool to the **same** published version (see [NuGet](https://www.nuget.org/packages/Nefarius.Tools.SignRelay.MSBuild) / [releases](https://github.com/nefarius/SignRelay/releases)).

```xml
<ItemGroup>
  <PackageReference Include="Nefarius.Tools.SignRelay.MSBuild" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

```bash
dotnet tool install --global Nefarius.Tools.SignRelay --version 1.0.0
dotnet publish -c Release \
  /p:SignRelayEnabled=true \
  /p:SignRelayServer=https://relay.example.com
```

Set `SIGN_RELAY_CI_TOKEN`. Default is **off** (`SignRelayEnabled=false`) so local builds do not hit the network. Signs **in-place** after `Publish`. Optional: `<SignRelayFile Include="..." />`. Pack/zip **after** `SignRelaySign`.
