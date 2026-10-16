<#
.SYNOPSIS
  Builds MdViewer into a single self-contained-looking .exe.

.DESCRIPTION
  Publishes a framework-dependent single-file executable for win-x64.
  The result needs the .NET 9 Desktop Runtime and the Edge WebView2 Runtime,
  both of which are already present on an up-to-date Windows 11 machine.
#>
param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Intermediate build files go outside the project folder: it sits on a synced
# drive, and the sync client locks files mid-build.
$artifacts = Join-Path $env:TEMP "MdViewer-build"
$out = Join-Path $PSScriptRoot "bin\$Configuration\publish"

dotnet publish MdViewer.csproj -c $Configuration --artifacts-path $artifacts -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exe = Join-Path $out "MdViewer.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Built: $exe  ($size MB)" -ForegroundColor Green
