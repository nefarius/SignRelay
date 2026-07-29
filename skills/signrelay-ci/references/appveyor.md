# AppVeyor

Secure variables are unavailable on PR builds — skip signing when `$env:APPVEYOR_PULL_REQUEST_NUMBER` is set.

```yaml
install:
  - ps: dotnet tool install --global Nefarius.Tools.SignRelay

after_build:
  - ps: |
      if ($env:APPVEYOR_PULL_REQUEST_NUMBER) {
        Write-Host "Skipping SignRelay on PR build."
        exit 0
      }
      $files = @(Get-ChildItem -Path .\artifacts\*.exe -File | ForEach-Object { $_.FullName })
      if ($files.Count -eq 0) { throw "No files to sign" }
      & signrelay submit --server https://relay.example.com --token $env:SIGN_RELAY_CI_TOKEN --output .\signed @files
      if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Ensure the AppVeyor job timeout exceeds `signrelay --timeout` (default 45 minutes).
