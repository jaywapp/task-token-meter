<#
.SYNOPSIS
    Verifies the installer scripts: clean install, update, Hook path stability, rollback and uninstall.

.DESCRIPTION
    Runs install.ps1 and uninstall.ps1 against a temporary install root. The user PATH, the real provider
    settings and the real ledger are never touched: the tests pass -SkipPathUpdate and an explicit
    --settings path.

.PARAMETER ArchivePath
    A package produced by Build-WindowsPackage.ps1. When omitted the package is built first.
#>
[CmdletBinding()]
param(
    [string]$ArchivePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$script:failures = 0
$script:checks = 0

function Test-Condition {
    param([string]$Name, [bool]$Condition, [string]$Detail = "")
    $script:checks++
    if ($Condition) {
        Write-Host "  PASS  $Name"
    }
    else {
        $script:failures++
        Write-Host "  FAIL  $Name $Detail"
    }
}

$packagingRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $packagingRoot ".."))
$installScript = Join-Path $packagingRoot "install.ps1"
$uninstallScript = Join-Path $packagingRoot "uninstall.ps1"

if (-not $ArchivePath) {
    $buildRoot = Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-testpkg-" + [Guid]::NewGuid().ToString("N"))
    Write-Host "==> Building a package into $buildRoot"
    & (Join-Path $packagingRoot "Build-WindowsPackage.ps1") -OutputRoot $buildRoot | Out-Null
    $ArchivePath = Join-Path $buildRoot "task-token-meter-win-x64.zip"
}
if (-not (Test-Path -LiteralPath $ArchivePath)) { throw "Package not found: $ArchivePath" }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-installer-test-" + [Guid]::NewGuid().ToString("N"))
$installRoot = Join-Path $sandbox "Programs\TaskTokenMeter"
$settingsPath = Join-Path $sandbox "claude-settings.json"
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

try {
    Write-Host "==> Clean install"
    & $installScript -Version "0.0.0-test.1" -ArchivePath $ArchivePath -InstallRoot $installRoot -SkipPathUpdate | Out-Null
    $firstVersionDirectory = Join-Path $installRoot "versions\0.0.0-test.1"
    $shim = Join-Path $installRoot "bin\task-token-meter.cmd"
    $statePath = Join-Path $installRoot "install-state.json"
    Test-Condition "version directory exists" (Test-Path -LiteralPath (Join-Path $firstVersionDirectory "task-token-meter.exe"))
    Test-Condition "shim exists" (Test-Path -LiteralPath $shim)
    Test-Condition "install state exists" (Test-Path -LiteralPath $statePath)
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Test-Condition "install state records the version" ($state.version -eq "0.0.0-test.1") $state.version

    Write-Host "==> Shim runs the CLI"
    $versionOutput = & $shim version 2>&1
    Test-Condition "shim exit code is 0" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    Test-Condition "shim prints a version" (($versionOutput -join "") -match "task-token-meter") ($versionOutput -join "")
    $jsonOutput = & $shim version --json 2>&1
    Test-Condition "version --json is a single object" ((($jsonOutput -join "") | ConvertFrom-Json).version.Length -gt 0)

    Write-Host "==> Hook entries record the shim"
    $installedExecutable = Join-Path $firstVersionDirectory "task-token-meter.exe"
    & $installedExecutable hook install --provider claude --provider-version 2.1.278 --settings $settingsPath --json | Out-Null
    Test-Condition "hook install exit code is 0" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    $settingsText = Get-Content -LiteralPath $settingsPath -Raw
    Test-Condition "hook command references the shim" ($settingsText.Contains($shim.Replace("\", "\\")))
    Test-Condition "hook command avoids the version directory" (-not $settingsText.Contains("versions\\0.0.0-test.1"))

    Write-Host "==> Update to a new version"
    & $installScript -Version "0.0.0-test.2" -ArchivePath $ArchivePath -InstallRoot $installRoot -SkipPathUpdate | Out-Null
    $secondVersionDirectory = Join-Path $installRoot "versions\0.0.0-test.2"
    Test-Condition "new version directory exists" (Test-Path -LiteralPath $secondVersionDirectory)
    Test-Condition "previous version directory was removed" (-not (Test-Path -LiteralPath $firstVersionDirectory))
    $updatedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Test-Condition "install state records the new version" ($updatedState.version -eq "0.0.0-test.2") $updatedState.version
    & $shim version | Out-Null
    Test-Condition "shim still runs after the update" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

    Write-Host "==> Hook entries survive the update"
    $statusJson = & (Join-Path $secondVersionDirectory "task-token-meter.exe") hook status --provider claude --settings $settingsPath --json 2>&1
    $status = ($statusJson -join "") | ConvertFrom-Json
    Test-Condition "hook status reports installed" ($status.state -eq "Installed") $status.state
    Test-Condition "hook executable is still available" ($status.executableAvailable -eq $true)
    Test-Condition "hook executable is the shim" ($status.executablePath -eq $shim) $status.executablePath

    Write-Host "==> Failed install keeps the active version"
    $missingArchive = Join-Path $sandbox "missing.zip"
    $failed = $false
    try { & $installScript -Version "0.0.0-test.3" -ArchivePath $missingArchive -InstallRoot $installRoot -SkipPathUpdate | Out-Null }
    catch { $failed = $true }
    Test-Condition "install fails for a missing archive" $failed
    Test-Condition "active version is unchanged" (Test-Path -LiteralPath (Join-Path $secondVersionDirectory "task-token-meter.exe"))
    & $shim version | Out-Null
    Test-Condition "shim still runs after the failed install" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

    Write-Host "==> Uninstall"
    & $uninstallScript -InstallRoot $installRoot -DataRoot (Join-Path $sandbox "data") | Out-Null
    Test-Condition "install root was removed" (-not (Test-Path -LiteralPath $installRoot))
    Test-Condition "provider settings were kept" (Test-Path -LiteralPath $settingsPath)
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("Checks: " + $script:checks + ", failures: " + $script:failures)
if ($script:failures -gt 0) { exit 1 }
exit 0
