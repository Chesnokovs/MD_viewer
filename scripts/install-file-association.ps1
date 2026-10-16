<#
.SYNOPSIS
  Makes MdViewer the app that opens Markdown files for the current user.

.DESCRIPTION
  Writes a ProgId and file extension entries under HKEY_CURRENT_USER only,
  so no administrator rights are needed and nothing changes for other users.
  Run uninstall-file-association.ps1 to undo it.

.EXAMPLE
  .\install-file-association.ps1 -Exe "C:\Tools\MdViewer\MdViewer.exe"
#>
param(
  [string]$Exe = "",
  [string[]]$Extensions = @(".md", ".markdown", ".mdown", ".mkd", ".mdtext")
)

$ErrorActionPreference = "Stop"

if ($Exe -eq "") {
  $Exe = Join-Path (Split-Path $PSScriptRoot -Parent) "bin\Release\publish\MdViewer.exe"
}
if (-not (Test-Path $Exe)) { throw "MdViewer.exe not found at $Exe (build it with build.ps1 or pass -Exe)" }
$Exe = (Resolve-Path $Exe).Path

$progId = "MdViewer.Markdown"
$classes = "HKCU:\Software\Classes"

New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
New-Item -Path "$classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId" -Name "(Default)" -Value "Markdown Document"
Set-ItemProperty -Path "$classes\$progId\DefaultIcon" -Name "(Default)" -Value "$Exe,0"
Set-ItemProperty -Path "$classes\$progId\shell\open\command" -Name "(Default)" -Value "`"$Exe`" `"%1`""

foreach ($ext in $Extensions) {
  New-Item -Path "$classes\$ext\OpenWithProgids" -Force | Out-Null
  Set-ItemProperty -Path "$classes\$ext" -Name "(Default)" -Value $progId
  New-ItemProperty -Path "$classes\$ext\OpenWithProgids" -Name $progId -Value ([byte[]]@()) -PropertyType None -Force | Out-Null
  Write-Host "Registered $ext" -ForegroundColor Green
}

# Ask Explorer to pick up the new associations.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Shell {
  [DllImport("shell32.dll")] public static extern void SHChangeNotify(int id, uint flags, IntPtr a, IntPtr b);
}
"@
[Shell]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ""
Write-Host "MdViewer is now registered for Markdown files." -ForegroundColor Cyan
Write-Host "If Windows still opens another app, right-click a .md file," -ForegroundColor DarkGray
Write-Host "choose 'Open with' - 'Choose another app' - MdViewer - 'Always'." -ForegroundColor DarkGray
