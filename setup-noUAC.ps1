# One-time elevated setup: register NextBoot as a Scheduled Task with
# HIGHEST run level, then repoint the desktop shortcut to schtasks /run.
# After this, launching the tool never shows a UAC prompt again.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "NextBoot\bin\Release\net10.0-windows\NextBoot.exe"
if (-not (Test-Path $exe)) { throw "exe not found: $exe (build first: dotnet build -c Release)" }

# 1) register task (ondemand; highest run level = no UAC when started via /run)
schtasks /create /tn "NextBoot" /tr "`"$exe`"" /sc ondemand /rl highest /f
if ($LASTEXITCODE -ne 0) { throw "schtasks create failed: $LASTEXITCODE" }

# 2) repoint desktop shortcut
$ws = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("Desktop")
$lnk = $ws.CreateShortcut("$desktop\NextBoot.lnk")
$lnk.TargetPath = "$env:SystemRoot\System32\schtasks.exe"
$lnk.Arguments = "/run /tn NextBoot"
$lnk.WorkingDirectory = Split-Path $exe
$lnk.IconLocation = "$exe,0"
$lnk.Description = "NextBoot - UEFI boot entry selector (no UAC via scheduled task)"
$lnk.Save()

Write-Host "DONE. Task registered and shortcut repointed."
Write-Host "Launching the tool once to verify..."
schtasks /run /tn "NextBoot"
