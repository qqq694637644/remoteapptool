[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$libraryPath = Join-Path $root "remoteapplib\bin\$Configuration\RemoteAppLib.dll"
$hostPath = Join-Path $root "RemoteAppSessionHost\bin\$Configuration\RemoteAppSessionHost.exe"

foreach ($path in @($libraryPath, $hostPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Build the solution before running this test. Missing: $path"
    }
}

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$What)
    if ($Expected -ne $Actual) {
        throw "$What expected '$Expected', got '$Actual'."
    }
}

$flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$library = [Reflection.Assembly]::LoadFrom($libraryPath)
$hostAssembly = [Reflection.Assembly]::LoadFrom($hostPath)
$modeReader = $library.GetType('RemoteAppLib.SystemRemoteApps', $true).GetMethod('ReadSessionMode', $flags)
$hostType = $hostAssembly.GetType('RemoteAppSessionHost.StrictAppConfig', $true)
$applyMode = $hostType.GetMethod('ApplyMode', $flags)
$optionsType = $hostAssembly.GetType('RemoteAppSessionHost.SessionHostOptions', $true)

if ($null -eq $modeReader -or $null -eq $applyMode) {
    throw 'Missing schema v2 session mode reader or policy resolver.'
}

# A private, throwaway HKCU registry key verifies the same schema reader used by GetApp.
$testKeyPath = "Software\RemoteAppTool\SessionModeTests\$([Guid]::NewGuid().ToString('N'))"
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath)

try {
    $key.SetValue('RemoteAppToolSchemaVersion', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
    foreach ($mode in @(1, 2, 1)) {
        $key.SetValue('RemoteAppToolSessionMode', $mode, [Microsoft.Win32.RegistryValueKind]::DWord)
        $resolvedMode = $modeReader.Invoke($null, [object[]]@($key, 'TestApp'))
        Assert-Equal $mode ([int]$resolvedMode) 'Stored session mode'
    }

    $key.SetValue('RemoteAppToolStrictSession', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    $rejectedLegacy = $false
    try {
        [void]$modeReader.Invoke($null, [object[]]@($key, 'TestApp'))
    }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        $rejectedLegacy = $cause -is [InvalidOperationException]
    }
    Assert-Equal $true $rejectedLegacy 'Obsolete schema rejection'

    $key.DeleteValue('RemoteAppToolStrictSession', $false)
    $key.SetValue('RemoteAppToolSchemaVersion', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    $rejectedV1 = $false
    try {
        [void]$modeReader.Invoke($null, [object[]]@($key, 'TestApp'))
    }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        $rejectedV1 = $cause -is [InvalidOperationException]
    }
    Assert-Equal $true $rejectedV1 'Schema v1 rejection'

    $key.DeleteValue('RemoteAppToolSchemaVersion', $false)
    $key.DeleteValue('RemoteAppToolSessionMode', $false)
    $normalMode = $modeReader.Invoke($null, [object[]]@($key, 'TestApp'))
    Assert-Equal 0 ([int]$normalMode) 'Normal session mode'
}
finally {
    $key.Close()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKeyPath)
}

# Reuse the SAME options object to catch policy values leaking across mode changes.
$options = [Activator]::CreateInstance($optionsType, $true)
foreach ($testCase in @(
    @{ Mode = 1; Persistent = $false; Termination = 0; Disconnect = 8000 },
    @{ Mode = 2; Persistent = $true;  Termination = 1; Disconnect = -1 },
    @{ Mode = 1; Persistent = $false; Termination = 0; Disconnect = 8000 }
)) {
    [void]$applyMode.Invoke($null, [object[]]@([int]$testCase.Mode, $options, [int]8000))
    Assert-Equal $testCase.Persistent ($optionsType.GetProperty('PersistentSession').GetValue($options, $null)) 'PersistentSession'
    Assert-Equal $testCase.Termination ($optionsType.GetProperty('TerminationMode').GetValue($options, $null)) 'TerminationMode'
    Assert-Equal $testCase.Disconnect ($optionsType.GetProperty('DisconnectGraceMs').GetValue($options, $null)) 'DisconnectGraceMs'
}

Write-Host 'Session mode contract passed: Strict -> Persistent -> Strict; schema v1 rejected.' -ForegroundColor Green
