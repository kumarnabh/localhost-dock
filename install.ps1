<#
.SYNOPSIS
  Builds Localhost Dock and installs it for the current user.

.DESCRIPTION
  Publishes a Release build to %LOCALAPPDATA%\Programs\LocalhostDock, adds a Start Menu shortcut,
  and starts the app. No admin rights needed. Your registry (%APPDATA%\LocalhostDock\apps.json) is
  never touched by install or uninstall.

.PARAMETER Uninstall
  Removes the installed files, the Start Menu shortcut, the "Start with Windows" entry and the
  Explorer "Add to Localhost Dock" menu entries.
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$target   = Join-Path $env:LOCALAPPDATA 'Programs\LocalhostDock'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Localhost Dock.lnk'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process LocalhostDock -ErrorAction SilentlyContinue | Stop-Process -Force

if ($Uninstall) {
    if (Test-Path $target)   { Remove-Item $target -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Remove-ItemProperty -Path $runKey -Name LocalhostDock -ErrorAction SilentlyContinue
    foreach ($key in 'Directory', 'SystemFileAssociations\.cmd', 'SystemFileAssociations\.bat', 'SystemFileAssociations\.ps1') {
        $path = "HKCU:\Software\Classes\$key\shell\LocalhostDock.Add"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Write-Host 'Localhost Dock uninstalled. Your apps.json was left in place.'
    return
}

$project = Join-Path $PSScriptRoot 'src\LocalhostDock\LocalhostDock.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $target
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $target 'LocalhostDock.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Description = 'Tray widget for your local apps and dev servers'
$lnk.Save()

Start-Process $exe -ArgumentList '--show'
Write-Host "Installed to $target and started. Look for the dock icon in the system tray."
