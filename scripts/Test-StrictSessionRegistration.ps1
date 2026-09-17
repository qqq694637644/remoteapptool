[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Alias,

    [string]$RdpPath
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)
    $script:failures.Add($Message)
}

$registryCandidates = @(
    "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications\$Alias",
    "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList\Applications\$Alias"
)

$appKeyPath = $registryCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $appKeyPath) {
    throw "RemoteApp alias '$Alias' was not found in the native or Wow6432Node TSAppAllowList registry paths."
}

$app = Get-ItemProperty -Path $appKeyPath

if ([int]$app.RemoteAppToolStrictSession -ne 1) {
    Add-Failure 'RemoteAppToolStrictSession is not enabled.'
}

$strictId = [Guid]::Empty
if (-not [Guid]::TryParse([string]$app.RemoteAppToolStrictId, [ref]$strictId)) {
    Add-Failure 'RemoteAppToolStrictId is missing or is not a valid GUID.'
}

if ([string]::IsNullOrWhiteSpace([string]$app.RemoteAppToolTargetPath)) {
    Add-Failure 'RemoteAppToolTargetPath is missing.'
}

if ([string]::IsNullOrWhiteSpace([string]$app.Path)) {
    Add-Failure 'Published Path is missing.'
}
elseif ([IO.Path]::GetFileName([string]$app.Path) -ine 'RemoteAppSessionHost.exe') {
    Add-Failure "Published Path does not point to RemoteAppSessionHost.exe: $($app.Path)"
}
elseif ($strictId -ne [Guid]::Empty -and [IO.Path]::GetFileName([IO.Path]::GetDirectoryName([string]$app.Path)) -ine $strictId.ToString('D')) {
    Add-Failure 'Published launcher parent directory does not match RemoteAppToolStrictId.'
}

if (-not [string]::IsNullOrWhiteSpace([string]$app.Path) -and -not (Test-Path -LiteralPath ([string]$app.Path))) {
    Add-Failure "Published Strict launcher does not exist: $($app.Path)"
}

if ([int]$app.RemoteAppToolSchemaVersion -ne 1) {
    Add-Failure "Unexpected Strict schema version: $($app.RemoteAppToolSchemaVersion)"
}

if ($RdpPath) {
    if (-not (Test-Path -LiteralPath $RdpPath)) {
        Add-Failure "RDP file does not exist: $RdpPath"
    }
    else {
        $rdpLines = Get-Content -LiteralPath $RdpPath
        $connectionSharingLines = @($rdpLines | Where-Object { $_ -match '^disableconnectionsharing:i:' })

        if ($connectionSharingLines.Count -ne 1) {
            Add-Failure "RDP must contain exactly one disableconnectionsharing property; found $($connectionSharingLines.Count)."
        }
        elseif ($connectionSharingLines[0] -ne 'disableconnectionsharing:i:1') {
            Add-Failure "RDP does not force a new connection: $($connectionSharingLines[0])"
        }

        $expectedProgram = "remoteapplicationprogram:s:||$Alias"
        if ($rdpLines -notcontains $expectedProgram) {
            Add-Failure "RDP does not publish the expected alias: $expectedProgram"
        }
    }
}

$result = [pscustomobject]@{
    Alias = $Alias
    RegistryPath = $appKeyPath
    StrictId = [string]$app.RemoteAppToolStrictId
    TargetPath = [string]$app.RemoteAppToolTargetPath
    PublishedPath = [string]$app.Path
    CloseGraceMs = [int]$app.RemoteAppToolCloseGraceMs
    DisconnectBehavior = [int]$app.RemoteAppToolDisconnectBehavior
    DisconnectGraceMs = [int]$app.RemoteAppToolDisconnectGraceMs
    RdpPath = $RdpPath
    Passed = ($failures.Count -eq 0)
}

$result | Format-List

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failures:' -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host ''
Write-Host 'Strict App Session registration preflight passed.' -ForegroundColor Green
