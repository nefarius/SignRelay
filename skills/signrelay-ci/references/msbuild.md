# MSBuild

```xml
<ItemGroup>
  <PackageReference Include="Nefarius.Tools.SignRelay.MSBuild" Version="*" PrivateAssets="all" />
</ItemGroup>
```

```bash
dotnet tool install --global Nefarius.Tools.SignRelay
dotnet publish -c Release \
  /p:SignRelayEnabled=true \
  /p:SignRelayServer=https://relay.example.com
```

Set `SIGN_RELAY_CI_TOKEN`. Default is **off** (`SignRelayEnabled=false`) so local builds do not hit the network. Signs **in-place** after `Publish`. Optional: `<SignRelayFile Include="..." />`. Pack/zip **after** `SignRelaySign`.
