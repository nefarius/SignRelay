# Raw CLI

```bash
dotnet tool install --global Nefarius.Tools.SignRelay

# Expand globs in the shell — the CLI does not.
signrelay submit \
  --server https://relay.example.com \
  --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed \
  ./artifacts/MyApp.exe
```

Dry-run:

```bash
signrelay submit --server https://relay.example.com --token "$SIGN_RELAY_CI_TOKEN" \
  --output ./signed --dry-run ./artifacts/MyApp.exe
```

Exit codes: 0 ok; 2 bad args; 3 server reject; 4 agent fail; 5 timeout; 6 SSE/proxy.
