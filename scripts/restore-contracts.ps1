param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packagesDir = Join-Path $repoRoot ".packages"
New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

$packageFile = "PCPulse.Contracts.$Version.nupkg"
$destination = Join-Path $packagesDir $packageFile
if (Test-Path -LiteralPath $destination -PathType Leaf) {
    Write-Host "PCPulse.Contracts $Version already present at $destination"
    exit 0
}

$tag = "contracts-v$Version"
$url = "https://github.com/rkfsociety/PCPulse/releases/download/$tag/$packageFile"
Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing

if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or (Get-Item -LiteralPath $destination).Length -eq 0) {
    throw "Не удалось скачать $packageFile из $url"
}

Write-Host "Saved: $destination"
