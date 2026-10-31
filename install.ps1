<#
.SYNOPSIS
  Builds Localhost Dock and installs (or upgrades) it for the current user, with rollback.

.DESCRIPTION
  1. Publishes a Release build into a staging folder (nothing installed is touched yet).
  2. Runs the staged build's self-test. If it fails, the install stops and your current version keeps running.
  3. Only then stops the running dock, keeps the current install as "LocalhostDock.previous", and moves the
     new build into %LOCALAPPDATA%\Programs\LocalhostDock.
  4. Starts it and checks it stays up. If it doesn't, the previous version is restored and restarted.

  No admin rights needed. Your registry (%APPDATA%\LocalhostDock\apps.json) is never touched by install,
  upgrade, rollback or uninstall.

.PARAMETER SelfContained
  Bundle the .NET runtime (bigger, but needs no separate .NET Desktop Runtime install).

.PARAMETER Rollback
  Restore the previous version kept by the last upgrade.

.PARAMETER Uninstall
  Removes the installed files, the Start Menu shortcut, the "Start with Windows" entry and the
  Explorer "Add to Localhost Dock" menu entries.
#>
param([switch]$Uninstall, [switch]$Rollback, [switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$target   = Join-Path $programs 'LocalhostDock'
$previous = Join-Path $programs 'LocalhostDock.previous'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Localhost Dock.lnk'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-Dock {
    # Only the installed dock, identified by its path; never other processes. Ask it to exit (v0.3+ understands
    # --exit and removes its tray icon cleanly); force only if it doesn't.
    $running = @(Get-Process LocalhostDock -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -eq 0) { return }
    $exe = Join-Path $target 'LocalhostDock.exe'
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--exit' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue }
    foreach ($p in $running) { if (-not $p.WaitForExit(5000)) { $p.Kill() } }
}

function Start-AndVerify([string]$exe) {
    $p = Start-Process $exe -ArgumentList '--show' -PassThru
    Start-Sleep -Seconds 4
    return -not $p.HasExited
}

if ($Uninstall) {
    Stop-Dock
    foreach ($dir in $target, $previous) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Remove-ItemProperty -Path $runKey -Name LocalhostDock -ErrorAction SilentlyContinue
    foreach ($key in 'Directory', 'SystemFileAssociations\.cmd', 'SystemFileAssociations\.bat', 'SystemFileAssociations\.ps1') {
        $path = "HKCU:\Software\Classes\$key\shell\LocalhostDock.Add"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Write-Host 'Localhost Dock uninstalled. Your apps.json was left in place.'
    return
}

if ($Rollback) {
    if (-not (Test-Path $previous)) { throw 'There is no previous version to roll back to.' }
    Stop-Dock
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Move-Item $previous $target
    Start-Process (Join-Path $target 'LocalhostDock.exe') -ArgumentList '--show'
    Write-Host 'Rolled back to the previous version.'
    return
}

# ---- 1. build into staging
$staging = Join-Path ([IO.Path]::GetTempPath()) ("LocalhostDock-staging-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$project = Join-Path $PSScriptRoot 'src\LocalhostDock\LocalhostDock.csproj'
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish $project -c Release -r win-x64 --self-contained $sc -p:PublishSingleFile=true -o $staging
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed; nothing was changed.' }

# ---- 2. validate before touching the installed version
$newExe = Join-Path $staging 'LocalhostDock.exe'
if (-not (Test-Path $newExe)) { throw "The build didn't produce LocalhostDock.exe; nothing was changed." }
$test = Start-Process $newExe -ArgumentList '--selftest' -PassThru -Wait -WindowStyle Hidden
if ($test.ExitCode -ne 0) {
    Remove-Item $staging -Recurse -Force
    throw "The new build failed its self-test (exit code $($test.ExitCode)); your current version was left running."
}
$version = (Get-Item $newExe).VersionInfo.ProductVersion

# ---- 3. swap, keeping the current version for rollback
Stop-Dock
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
if (Test-Path $target) { Move-Item $target $previous }
Move-Item $staging $target

$exe = Join-Path $target 'LocalhostDock.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Description = 'Tray widget for your local apps and dev servers'
$lnk.Save()

# ---- 4. start and verify, rolling back if it won't run
if (Start-AndVerify $exe) {
    Write-Host "Installed Localhost Dock $version to $target and started it. Look for the dock icon in the system tray."
    if (Test-Path $previous) { Write-Host "The previous version is kept; '.\install.ps1 -Rollback' restores it." }
    return
}

Write-Warning "The new version exited right after starting."
if (Test-Path $previous) {
    Remove-Item $target -Recurse -Force
    Move-Item $previous $target
    Start-Process $exe -ArgumentList '--show'
    throw "Rolled back to the previous version, which is running again."
}
throw "There was no previous version to roll back to."
