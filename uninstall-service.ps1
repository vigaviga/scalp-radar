<#
.SYNOPSIS
    Stops and removes the ScalpRadar Windows service.

.DESCRIPTION
    Run from an elevated PowerShell:

        .\uninstall-service.ps1               # remove the service, keep the files in -InstallDir
        .\uninstall-service.ps1 -RemoveFiles  # also delete -InstallDir (binaries and logs)

    Removing the service also removes its stored secrets (the Environment value under the
    service's registry key). The Postgres data in Docker is not touched.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ScalpRadar',
    [string]$ServiceName = 'ScalpRadar',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "Service $ServiceName is not installed."
}
else {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName (it flushes queued alerts first)..."
        Stop-Service -Name $ServiceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))
    }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit code $LASTEXITCODE)" }

    $deadline = (Get-Date).AddSeconds(30)
    while ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Service $ServiceName removed (with its stored secrets)."
}

if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "Deleted $InstallDir."
    }
}
elseif (Test-Path $InstallDir) {
    Write-Host "Files kept in $InstallDir (use -RemoveFiles to delete them)."
}
