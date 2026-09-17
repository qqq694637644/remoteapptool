[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this test with Windows PowerShell 5.1 (powershell.exe), not PowerShell 7. The project targets .NET Framework 4.0.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$msbuildCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
    throw '.NET Framework 4.x MSBuild was not found.'
}

$hostProject = Join-Path $repoRoot 'RemoteAppSessionHost\RemoteAppSessionHost.vbproj'
$libProject = Join-Path $repoRoot 'remoteapplib\RemoteAppLib.vbproj'

& $msbuild $hostProject /nologo /m /t:Build "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw "RemoteAppSessionHost build failed with exit code $LASTEXITCODE" }

& $msbuild $libProject /nologo /m /t:Build "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw "RemoteAppLib build failed with exit code $LASTEXITCODE" }

$hostExe = Join-Path $repoRoot "RemoteAppSessionHost\bin\$Configuration\RemoteAppSessionHost.exe"
$libDir = Join-Path $repoRoot "remoteapplib\bin\$Configuration"
$libDll = Join-Path $libDir 'RemoteAppLib.dll'
Copy-Item $hostExe (Join-Path $libDir 'RemoteAppSessionHost.exe') -Force

$assembly = [Reflection.Assembly]::LoadFrom($libDll)
$deploymentType = $assembly.GetType('RemoteAppLib.StrictSessionDeployment', $true)
$strictId = [Guid]::NewGuid().ToString('D')

try {
    $launcherPath = $deploymentType.GetMethod('EnsureLauncher').Invoke($null, @($strictId))
    if (-not (Test-Path $launcherPath)) {
        throw "Launcher deployment returned a missing path: $launcherPath"
    }

    $sourceHash = (Get-FileHash $hostExe -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash $launcherPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw 'Deployed launcher hash does not match the source launcher.'
    }

    $launcherDirectory = Split-Path -Parent $launcherPath
    $acl = Get-Acl $launcherDirectory
    $usersRule = $acl.Access | Where-Object {
        $_.IdentityReference.Value -match '\\Users$|\\用户$' -and
        $_.AccessControlType -eq 'Allow'
    } | Select-Object -First 1

    if (-not $usersRule) {
        throw 'Built-in Users read/execute ACL was not found on the strict launcher directory.'
    }

    if (($usersRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -ne 0 -or
        ($usersRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -ne 0 -or
        ($usersRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne 0) {
        throw "Built-in Users unexpectedly has write-capable rights: $($usersRule.FileSystemRights)"
    }

    Write-Host "Deployment OK: $launcherPath"
    Write-Host "SHA256 matches source: $sourceHash"
    Write-Host "Users ACL: $($usersRule.FileSystemRights)"
}
finally {
    $deploymentType.GetMethod('CleanupLauncher').Invoke($null, @($strictId)) | Out-Null
}

$strictRoot = $deploymentType.GetMethod('GetStrictRootDirectory').Invoke($null, @())
$testDirectory = Join-Path $strictRoot $strictId
if (Test-Path $testDirectory) {
    throw "Test launcher directory was not cleaned up: $testDirectory"
}

Write-Host 'Cleanup OK.'
