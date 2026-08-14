<#
.SYNOPSIS
    Builds the Connection Dashboard and stages it for embedding in the exe.

.DESCRIPTION
    The dashboard is the shared UI for ConnectionDoctor (Windows) and TBDoctor
    (macOS); it lives in its own repo and is consumed here as a build artifact.
    The staged output is git-ignored on purpose — build output does not belong
    in this repo's history, and a stale committed copy would be worse than none.

    Node is needed to run this script. It is NOT needed to run ConnectionDoctor:
    the bundle is compiled into the exe, so users never see npm.

.PARAMETER DashboardPath
    Checkout of mhuot/connection-dashboard. Defaults to a sibling directory.
#>
[CmdletBinding()]
param(
    [string]$DashboardPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '..\connection-dashboard')
)

$ErrorActionPreference = 'Stop'

$app = Join-Path $DashboardPath 'app'
if (-not (Test-Path $app)) {
    throw "No dashboard checkout at '$app'. Clone mhuot/connection-dashboard beside this repo, or pass -DashboardPath."
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'npm is not on PATH. Node is required to build the dashboard bundle (but not to run ConnectionDoctor).'
}

Push-Location $app
try {
    if (-not (Test-Path (Join-Path $app 'node_modules'))) {
        Write-Host 'Installing dashboard dependencies...'
        npm install --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)" }
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

$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ConnectionDoctor\ui'
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $dist '*') -Destination $target -Recurse -Force

$files = Get-ChildItem $target -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("Staged {0} files ({1:N0} KB) into {2}" -f $files.Count, ($bytes / 1KB), $target)
Write-Host 'Rebuild ConnectionDoctor to embed them.'
