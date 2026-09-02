# Expands SignRelay Action `files` input lines (literal paths and globs).
# ** is globstar (zero or more directories). The static prefix before the first
# glob metacharacter is the search root — not Split-Path of the raw pattern,
# which treats `bin/**` as a literal directory.

function Get-SignRelayGlobSearchRoot([string]$Pattern) {
  $normalized = $Pattern.Replace('\', '/')
  $globAt = -1
  for ($i = 0; $i -lt $normalized.Length; $i++) {
    if ($normalized[$i] -eq '*' -or $normalized[$i] -eq '?') {
      $globAt = $i
      break
    }
  }
  if ($globAt -le 0) { return '.' }

  $slash = $normalized.LastIndexOf([char]'/', $globAt - 1)
  if ($slash -lt 0) { return '.' }
  if ($slash -eq 0) { return '/' }

  $prefix = $normalized.Substring(0, $slash)
  if ($prefix -match '^[A-Za-z]:$') {
    return $prefix + '\'
  }

  $isUnc = $Pattern.StartsWith('\\') -or $Pattern.StartsWith('//')
  $sep = if ($isUnc -or $Pattern.Contains('\')) { '\' } else { '/' }
  if ($isUnc) {
    return $sep + $sep + $prefix.TrimStart('/').Replace('/', $sep)
  }
  return $prefix.Replace('/', $sep)
}

function Get-SignRelayGlobRemainder([string]$Pattern) {
  $normalized = $Pattern.Replace('\', '/')
  $remainder = [System.Collections.Generic.List[string]]::new()
  $inGlob = $false
  foreach ($segment in ($normalized -split '/')) {
    if (-not $inGlob -and $segment -notmatch '[*?]') { continue }
    $inGlob = $true
    if ($segment -ne '') { $remainder.Add($segment) }
  }
  if ($remainder.Count -eq 0) { return '*' }
  return ($remainder -join '/')
}

function ConvertTo-SignRelayGlobRegex([string]$PatternFromRoot) {
  $p = $PatternFromRoot.Replace('\', '/').Trim('/')
  if ([string]::IsNullOrWhiteSpace($p)) { return '(?i)^.*$' }

  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.Append('(?i)^')
  $needSlash = $false
  foreach ($seg in ($p -split '/')) {
    if ($seg -eq '**') {
      [void]$sb.Append('(?:.*/)*')
      $needSlash = $false
      continue
    }
    if ($needSlash) { [void]$sb.Append('/') }
    $escaped = [regex]::Escape($seg).Replace('\*', '[^/]*').Replace('\?', '[^/]')
    [void]$sb.Append($escaped)
    $needSlash = $true
  }
  [void]$sb.Append('$')
  return $sb.ToString()
}

function Expand-SignRelayFiles([string]$Raw) {
  $resolved = [System.Collections.Generic.List[string]]::new()
  $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($line in ($Raw -split "`r?`n")) {
    $pattern = $line.Trim()
    if (-not $pattern) { continue }

    $hasGlob = $pattern.Contains('*') -or $pattern.Contains('?')
    if ($hasGlob) {
      $searchRoot = Get-SignRelayGlobSearchRoot $pattern
      if (-not (Test-Path -LiteralPath $searchRoot -PathType Container)) {
        throw "Glob parent directory does not exist: $searchRoot (from pattern '$pattern')"
      }

      $remainder = Get-SignRelayGlobRemainder $pattern
      $recurse = $remainder.Contains('**') -or $remainder.Contains('/')
      $leafFilter = Split-Path -Leaf ($remainder.Replace('/', [IO.Path]::DirectorySeparatorChar))
      $gci = @{
        LiteralPath = $searchRoot
        Filter      = $leafFilter
        File        = $true
        ErrorAction = 'SilentlyContinue'
      }
      # Passing -Recurse:$false is not the same as omitting -Recurse; depth 0
      # yields no files on some PowerShell versions.
      if ($recurse) { $gci.Recurse = $true }
      $candidates = @(Get-ChildItem @gci)
      $regex = ConvertTo-SignRelayGlobRegex $remainder
      $rootFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($searchRoot)
      $matchedFiles = @(
        $candidates | Where-Object {
          $rel = [IO.Path]::GetRelativePath($rootFull, $_.FullName).Replace('\', '/')
          $rel -match $regex
        }
      )

      if ($matchedFiles.Count -eq 0) {
        throw "Glob matched zero files: $pattern"
      }
      foreach ($m in $matchedFiles) {
        $full = $m.FullName
        if ($seen.Add($full)) { $resolved.Add($full) }
      }
    } else {
      $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($pattern)
      if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "File not found: $full"
      }
      if ($seen.Add($full)) { $resolved.Add($full) }
    }
  }
  if ($resolved.Count -eq 0) {
    throw "No files to sign after expanding inputs.files."
  }
  return $resolved
}
