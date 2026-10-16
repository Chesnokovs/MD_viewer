<#
.SYNOPSIS
  Removes the MdViewer file associations created by install-file-association.ps1.

.DESCRIPTION
  Only touches HKEY_CURRENT_USER. Extensions are left registered to whatever
  Windows falls back to once the MdViewer ProgId is gone.
#>
param(
  [string[]]$Extensions = @(".md", ".markdown", ".mdown", ".mkd", ".mdtext")
)

$ErrorActionPreference = "Stop"

$progId = "MdViewer.Markdown"
$classes = "HKCU:\Software\Classes"

foreach ($ext in $Extensions) {
  $key = "$classes\$ext"
  if (Test-Path $key) {
    $current = (Get-ItemProperty -Path $key -Name "(Default)" -ErrorAction SilentlyContinue)."(Default)"
    if ($current -eq $progId) {
      Set-ItemProperty -Path $key -Name "(Default)" -Value ""
    }
    $openWith = "$key\OpenWithProgids"
    if (Test-Path $openWith) {
      Remove-ItemProperty -Path $openWith -Name $progId -ErrorAction SilentlyContinue
    }
    Write-Host "Cleared $ext" -ForegroundColor Yellow
  }
}

if (Test-Path "$classes\$progId") {
  Remove-Item -Path "$classes\$progId" -Recurse -Force
  Write-Host "Removed ProgId $progId" -ForegroundColor Yellow
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Shell {
  [DllImport("shell32.dll")] public static extern void SHChangeNotify(int id, uint flags, IntPtr a, IntPtr b);
}
"@
[Shell]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ""
Write-Host "MdViewer file associations removed." -ForegroundColor Cyan
