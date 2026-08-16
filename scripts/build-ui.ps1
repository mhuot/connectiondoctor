<#
.SYNOPSIS
    Builds the Connection Dashboard once and stages it into both collectors.

.DESCRIPTION
    Stages dashboard/dist into
      windows/src/ConnectionDoctor/ui   (ConnectionDoctor, EmbeddedResource)
      macos/Sources/TBDoctor/ui         (TBDoctor, SwiftPM resource)
    The staged output is git-ignored on purpose — build output does not belong
    in history. Node is needed to run this script; it is NOT needed to run
    either collector, because the bundle is compiled into the binary.

.PARAMETER Target
    windows | macos | all (default all)
#>
[CmdletBinding()]
param(
    [ValidateSet('windows', 'macos', 'all')]
    [string]$Target = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'dashboard'

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'npm is not on PATH. Node is required to build the dashboard bundle (but not to run the collectors).'
}

Push-Location $app
try {
    if (-not (Test-Path (Join-Path $app 'node_modules'))) {
        Write-Host 'Installing dashboard dependencies...'
        npm ci --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
    }
    Write-Host 'Building dashboard...'
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$dist = Join-Path $app 'dist'
if (-not (Test-Path (Join-Path $dist 'index.html'))) {
    throw "Dashboard build produced no index.html at '$dist'."
}

function Stage([string]$dir) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    # Keep .gitkeep where present: it is what makes SwiftPM's .copy("ui") resolve in a clean checkout.
    Get-ChildItem $dir -Force | Where-Object { $_.Name -ne '.gitkeep' } | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $dist '*') -Destination $dir -Recurse -Force
    $files = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -ne '.gitkeep' }
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Staged {0} files ({1:N0} KB) into {2}" -f $files.Count, ($bytes / 1KB), $dir.Substring($root.Length + 1))
}

switch ($Target) {
    'windows' { Stage (Join-Path $root 'windows\src\ConnectionDoctor\ui') }
    'macos'   { Stage (Join-Path $root 'macos\Sources\TBDoctor\ui') }
    'all'     { Stage (Join-Path $root 'windows\src\ConnectionDoctor\ui'); Stage (Join-Path $root 'macos\Sources\TBDoctor\ui') }
}
Write-Host 'Rebuild the collector(s) to embed it.'
