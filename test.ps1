<#
.SYNOPSIS
  Runs the unit tests (tests\MdViewer.Tests).

.DESCRIPTION
  Intermediate files go to %TEMP%\MdViewer-build, like the other build scripts,
  so a synced project folder does not get locked mid-build.
#>
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$artifacts = Join-Path $env:TEMP "MdViewer-build"
dotnet test MdViewer.sln -c Release --artifacts-path $artifacts --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
