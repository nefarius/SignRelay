# GitHub Actions

```yaml
- uses: actions/setup-dotnet@v6
  with:
    dotnet-version: '10.0.x'

- name: Sign with SignRelay
  uses: nefarius/SignRelay@v1
  with:
    server: https://relay.example.com
    token: ${{ secrets.SIGN_RELAY_CI_TOKEN }}
    files: |
      ./artifacts/MyApp.exe
      ./artifacts/*.dll
      ./bin/**/*.exe
    output: ./signed
```

Dry-run (no network):

```yaml
- uses: nefarius/SignRelay@v1
  with:
    server: https://relay.example.com
    token: dry-run-token-0123456789abcdef0123456789
    files: ./artifacts/MyApp.exe
    output: ./signed
    dry-run: true
```

Inputs: `server`, `token`, `files` (globs OK — `*`, `?`, and `**` expanded by the action), `output` | `in-place`, `timeout`, `tool-version`, `dry-run`, `allow-insecure`, `skip-tool-install`.
