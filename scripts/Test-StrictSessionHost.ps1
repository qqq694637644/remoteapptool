[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Headless,

    [switch]$AllowLogoff
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'RemoteAppSessionHost\RemoteAppSessionHost.vbproj'
$msbuildCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $msbuild) {
    throw '.NET Framework 4.x MSBuild was not found.'
}

& $msbuild $project /nologo /m /t:Build "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) {
    throw "RemoteAppSessionHost build failed with exit code $LASTEXITCODE"
}

$hostExe = Join-Path $repoRoot "RemoteAppSessionHost\bin\$Configuration\RemoteAppSessionHost.exe"
if (-not (Test-Path $hostExe)) {
    throw "Build succeeded but host executable was not found: $hostExe"
}

$arguments = [System.Collections.Generic.List[string]]::new()
if ($Headless) {
    $arguments.Add('--target')
    $arguments.Add($env:ComSpec)
    $arguments.Add('--')
    $arguments.Add('/c')
    $arguments.Add('exit')
    $arguments.Add('0')
}
else {
    $arguments.Add('--target')
    $arguments.Add((Join-Path $env:WINDIR 'System32\notepad.exe'))
}

if (-not $AllowLogoff) {
    $arguments.Insert(2, '--no-logoff')
}

Write-Host "Launching $hostExe"
if (-not $AllowLogoff) {
    Write-Host 'Safety mode: --no-logoff is enabled.'
}
if (-not $Headless) {
    Write-Host 'Close the Notepad window. The session host should then exit.'
}

$process = Start-Process -FilePath $hostExe -ArgumentList $arguments.ToArray() -PassThru -Wait

$logDirectory = Join-Path $env:LOCALAPPDATA 'RemoteAppTool\StrictSession\logs'
$latestLog = Get-ChildItem -Path $logDirectory -Filter 'session-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($latestLog) {
    Write-Host "Latest lifecycle log: $($latestLog.FullName)"
    Get-Content -Path $latestLog.FullName
}
else {
    Write-Warning "No lifecycle log was found in $logDirectory"
}

if ($process.ExitCode -eq 20 -and -not $AllowLogoff) {
    Write-Warning 'Automatic logoff was safely disabled because the current session was detected as shared/non-exclusive.'
    exit 0
}

if ($process.ExitCode -ne 0) {
    throw "RemoteAppSessionHost exited with code $($process.ExitCode)"
}
