<#
.SYNOPSIS
  Builds MdViewer.msi — a per-user installer that needs no administrator rights.

.DESCRIPTION
  Publishes a self-contained ReadyToRun build into a folder (the .NET runtime is
  bundled, so the target machine needs none installed), then packages that folder
  with the WiX Toolset into dist\MdViewer-<version>.msi.

  One-time setup:
    dotnet tool install --global wix --version 5.*
    wix extension add -g WixToolset.UI.wixext/5.0.2

.PARAMETER Version
  Product version written into the MSI. Must be raised for every release,
  otherwise Windows refuses to upgrade an existing install.
#>
param(
  [string]$Version = "1.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$stage = Join-Path $root "bin\publish-installer"
# Intermediate build files go outside the project folder: it lives on a synced
# drive, and the sync client locks files mid-build.
$artifacts = Join-Path $env:TEMP "MdViewer-build"
$dist = Join-Path $root "dist"
$msi = Join-Path $dist "MdViewer-$Version.msi"

Write-Host "Publishing self-contained build..." -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
# Not single-file and precompiled (ReadyToRun): a compressed single file has to be
# unpacked on every launch, which is the slowest way to start the app.
dotnet publish MdViewer.csproj -c Release `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishReadyToRun=true `
  -p:Version=$Version `
  -p:DebugType=none `
  --artifacts-path $artifacts `
  -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

python (Join-Path $root "tools\make_license_rtf.py")

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Building installer..." -ForegroundColor Cyan
wix build (Join-Path $PSScriptRoot "MdViewer.wxs") `
  -ext WixToolset.UI.wixext `
  -arch x64 `
  -d Version=$Version `
  -d PublishDir="$stage" `
  -d IconSource="$root\assets\app.ico" `
  -d LicenseRtf="$PSScriptRoot\License.rtf" `
  -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed." }

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ""
Write-Host "Built: $msi  ($size MB)" -ForegroundColor Green
