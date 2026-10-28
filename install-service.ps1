<#
.SYNOPSIS
    Publishes ScalpRadar and installs (or updates) it as a Windows service.

.DESCRIPTION
    Run from an elevated PowerShell in the repo folder:

        .\install-service.ps1

    1. Stops the service if it is running, so its files can be replaced.
    2. Publishes the worker (Release) to -InstallDir.
    3. Asks for the Telegram bot token (hidden input) and chat id. The database password is read
       from .env. Press Enter at a prompt to keep the value from a previous install.
    4. Stores them as environment variables that only this service sees, in
       HKLM\SYSTEM\CurrentControlSet\Services\ScalpRadar\Environment.
       Nothing is written to the repo or to appsettings.json.
    5. Creates the service (automatic, delayed start, runs as LocalSystem), sets it to restart
       after a crash, and starts it.

    Run it again after pulling new code: it updates the files and keeps the secrets.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\ScalpRadar',
    [string]$ServiceName = 'ScalpRadar'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = $PSScriptRoot
$project = Join-Path $repo 'src\ScalpRadar.Worker\ScalpRadar.Worker.csproj'
$exe = Join-Path $InstallDir 'ScalpRadar.Worker.exe'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

function Read-Secret([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Get-ServiceEnvironment {
    $values = @{}
    $item = Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
    if ($null -ne $item) {
        foreach ($line in $item.Environment) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 1) }
        }
    }
    return $values
}

function Get-DotEnvValue([string]$Name) {
    $file = Join-Path $repo '.env'
    if (-not (Test-Path $file)) { return $null }
    foreach ($line in Get-Content $file) {
        if ($line -match "^\s*$Name\s*=\s*(.*?)\s*$") { return $Matches[1] }
    }
    return $null
}

# --- 1. Stop -------------------------------------------------------------------------------
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $service -and $service.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName (it flushes queued alerts first)..."
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))
}

# --- 2. Publish ----------------------------------------------------------------------------
Write-Host "Publishing to $InstallDir ..."
& dotnet publish $project -c Release -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }

# --- 3. Secrets ----------------------------------------------------------------------------
$current = Get-ServiceEnvironment

$keep = ''
if ($current.ContainsKey('Telegram__BotToken')) { $keep = ' (Enter keeps the current one)' }
$token = Read-Secret "Telegram bot token from @BotFather, input hidden$keep"
if (-not $token) {
    if (-not $current.ContainsKey('Telegram__BotToken')) { throw 'A bot token is required.' }
    $token = $current['Telegram__BotToken']
}
if ($token -notmatch '^\d+:[A-Za-z0-9_-]{30,}$') { throw 'That does not look like a bot token (expected 123456789:AA...).' }

$keep = ''
if ($current.ContainsKey('Telegram__ChatId')) { $keep = " (Enter keeps $($current['Telegram__ChatId']))" }
$chatId = Read-Host -Prompt "Telegram chat id$keep"
if (-not $chatId) {
    if (-not $current.ContainsKey('Telegram__ChatId')) { throw 'A chat id is required.' }
    $chatId = $current['Telegram__ChatId']
}
if ($chatId -notmatch '^-?\d+$') { throw 'The chat id must be a number (group ids start with -).' }

$dbPassword = Get-DotEnvValue 'POSTGRES_PASSWORD'
if (-not $dbPassword) {
    if ($current.ContainsKey('Database__Password')) { $dbPassword = $current['Database__Password'] }
    else { $dbPassword = Read-Secret 'Postgres password (the POSTGRES_PASSWORD you gave docker compose), input hidden' }
}
if (-not $dbPassword) { throw 'A database password is required.' }

$environment = @(
    'Telegram__Enabled=true',
    "Telegram__BotToken=$token",
    "Telegram__ChatId=$chatId",
    "Database__Password=$dbPassword"
)

# --- 4. Service ----------------------------------------------------------------------------
if ($null -eq $service) {
    Write-Host "Creating service $ServiceName ..."
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'ScalpRadar' `
        -Description 'Binance USD-M futures scalp-setup radar. Read-only market data; sends Telegram alerts. Never trades.' `
        -StartupType Automatic | Out-Null
}
else {
    Set-ItemProperty -Path $serviceKey -Name ImagePath -Value "`"$exe`""
}

# Delayed start gives the network (and Docker) time to come up after boot.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
# Restart 1 minute after any crash; the failure count resets after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
# Also treat a stop with a non-zero exit code (e.g. database unreachable at boot) as a failure.
& sc.exe failureflag $ServiceName 1 | Out-Null

New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
$token = $null
$dbPassword = $null

# --- 5. Start ------------------------------------------------------------------------------
Write-Host "Starting $ServiceName ..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 10
Get-Service -Name $ServiceName | Format-Table -AutoSize Status, Name, StartType

$log = Get-ChildItem -Path (Join-Path $InstallDir 'logs') -Filter 'scalpradar-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -ne $log) {
    Write-Host "Last log lines ($($log.FullName)):"
    Get-Content $log.FullName -Tail 15
}
Write-Host ''
Write-Host "Done. Logs: $InstallDir\logs. In Telegram, send /status to the bot."
