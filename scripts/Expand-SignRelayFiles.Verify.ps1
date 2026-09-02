$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Expand-SignRelayFiles.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('signrelay-glob-' + [guid]::NewGuid().ToString('N'))
$pushed = $false
try {
  New-Item -ItemType Directory -Force -Path (Join-Path $root 'bin' 'x86' 'deep') | Out-Null
  New-Item -ItemType Directory -Force -Path (Join-Path $root 'bin' 'ARM64') | Out-Null
  New-Item -ItemType Directory -Force -Path (Join-Path $root 'bin' 'x64' 'nested') | Out-Null
  Set-Content (Join-Path $root 'bin' 'x86' 'nefconc.exe') 'a'
  Set-Content (Join-Path $root 'bin' 'x86' 'nefconw.exe') 'a'
  Set-Content (Join-Path $root 'bin' 'x86' 'deep' 'skip.exe') 'a'
  Set-Content (Join-Path $root 'bin' 'ARM64' 'nefconc.exe') 'a'
  Set-Content (Join-Path $root 'bin' 'x64' 'nested' 'extra.exe') 'a'
  Set-Content (Join-Path $root 'bin' 'readme.txt') 'no'
  Set-Content (Join-Path $root 'bin' 'x86' 'helper.dll') 'no'

  Push-Location $root
  $pushed = $true
  $starstar = Expand-SignRelayFiles "./bin/**/*.exe"
  $names = $starstar | ForEach-Object { [IO.Path]::GetRelativePath($root, $_).Replace('\', '/') } | Sort-Object
  Write-Host '**/ matches:'
  $names | ForEach-Object { Write-Host "  $_" }
  if ($names.Count -ne 5) { throw "expected 5 exes, got $($names.Count)" }
  if ($names -notcontains 'bin/x86/nefconc.exe') { throw 'missing x86/nefconc.exe' }
  if ($names -notcontains 'bin/x64/nested/extra.exe') { throw 'missing nested extra.exe' }
  if ($names -notcontains 'bin/x86/deep/skip.exe') { throw 'missing x86/deep/skip.exe' }

  $oneLevel = Expand-SignRelayFiles './bin/x86/*.exe'
  if ($oneLevel.Count -ne 2) { throw "x86/*.exe expected 2, got $($oneLevel.Count)" }

  $literal = Expand-SignRelayFiles './bin/x86/nefconc.exe'
  if ($literal.Count -ne 1) { throw 'literal path failed' }

  $constrained = Expand-SignRelayFiles './bin/**/x86/*.exe'
  if ($constrained.Count -ne 2) { throw "**/x86/*.exe expected 2, got $($constrained.Count)" }

  $searchRoot = Get-SignRelayGlobSearchRoot './bin/**/*.exe'
  Write-Host "search root for ./bin/**/*.exe => $searchRoot"
  $norm = $searchRoot.Replace('\', '/')
  if ($norm -ne './bin') { throw "search root was '$searchRoot'" }

  $rootCases = @(
    @{ Pattern = 'C:\foo\bar\**\*.exe'; Expected = 'C:\foo\bar' }
    @{ Pattern = 'C:\**\*.exe'; Expected = 'C:\' }
    @{ Pattern = '\\server\share\dir\**\*.exe'; Expected = '\\server\share\dir' }
    @{ Pattern = '\\server\share\**\*.exe'; Expected = '\\server\share' }
    @{ Pattern = '/tmp/bin/**/*.exe'; Expected = '/tmp/bin' }
    @{ Pattern = '/**/*.exe'; Expected = '/' }
  )
  foreach ($case in $rootCases) {
    $actual = Get-SignRelayGlobSearchRoot $case.Pattern
    if ($actual -ne $case.Expected) {
      throw "search root for '$($case.Pattern)' was '$actual' (expected '$($case.Expected)')"
    }
  }

  $missingFailed = $false
  try {
    Expand-SignRelayFiles './missing/**/*.exe' | Out-Null
  } catch {
    $missingFailed = "$_" -match 'Glob parent directory does not exist'
    if (-not $missingFailed) { throw "wrong error: $_" }
    Write-Host "missing-root error OK: $_"
  }
  if (-not $missingFailed) { throw 'should have failed for missing root' }

  Write-Host 'All expander checks passed.'
} finally {
  if ($pushed) { Pop-Location }
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
