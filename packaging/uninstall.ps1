<#
.SYNOPSIS
    Removes a user installation of Task Token Meter.

.DESCRIPTION
    Deletes the installation directory and the PATH entry that the installer added. The local ledger and
    the provider Hook settings are kept unless they are removed explicitly.

    Managed Hook entries point at the installer shim, so removing the installation while Hook entries are
    still configured leaves those entries pointing at a missing file. The script refuses to continue in
    that case unless -Force is given.

.PARAMETER RemoveData
    Also deletes the global data root (%LOCALAPPDATA%\TaskTokenMeter), which contains the ledger.
    Workspace ledgers under <workspace>\.token-meter are never touched.

.PARAMETER Force
    Uninstalls even when provider Hook entries still reference this installation.

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\TaskTokenMeter"),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA "TaskTokenMeter"),
    [switch]$RemoveData,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

function Write-Step { param([string]$Message) Write-Host "==> $Message" }

function Remove-UserPath {
    param([string]$Directory)

    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not $current) { return $false }
    $entries = $current -split ';' | Where-Object { $_ }
    $kept = @($entries | Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') })
    if ($kept.Count -eq $entries.Count) { return $false }
    [Environment]::SetEnvironmentVariable("Path", ($kept -join ';'), "User")
    return $true
}

function Test-HookEntries {
    param([string]$ShimPath)

    $settingsFiles = @(
        (Join-Path $env:USERPROFILE ".claude\settings.json"),
        (Join-Path $env:USERPROFILE ".codex\hooks.json")
    )
    $found = @()
    foreach ($file in $settingsFiles) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $text = Get-Content -LiteralPath $file -Raw
        if ($text -and $text.Contains("task-token-meter-v1")) { $found += $file }
    }
    return $found
}

$installRoot = [IO.Path]::GetFullPath($InstallRoot)
if (-not (Test-Path -LiteralPath $installRoot)) {
    Write-Host "Task Token Meter is not installed at $installRoot. Nothing was changed."
    return
}

$statePath = Join-Path $installRoot "install-state.json"
$state = $null
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

$binDirectory = Join-Path $installRoot "bin"
$shimPath = Join-Path $binDirectory "task-token-meter.cmd"
$hookFiles = @(Test-HookEntries -ShimPath $shimPath)
if ($hookFiles.Count -gt 0 -and -not $Force) {
    $list = $hookFiles -join ", "
    throw "Managed Hook entries are still configured in: $list. Run 'task-token-meter hook uninstall --provider claude' and '--provider codex' first, or pass -Force."
}
if ($hookFiles.Count -gt 0) {
    Write-Warning "Hook entries remain in: $($hookFiles -join ', '). Remove them with 'hook uninstall' on a future installation."
}

Write-Step "Removing $installRoot"
Remove-Item -LiteralPath $installRoot -Recurse -Force

$pathEntry = $binDirectory
if ($state -and $state.PSObject.Properties.Name -contains "pathEntry" -and $state.pathEntry) { $pathEntry = $state.pathEntry }
if (Remove-UserPath -Directory $pathEntry) {
    Write-Step "Removed $pathEntry from the user PATH"
}

if ($RemoveData) {
    $dataRoot = [IO.Path]::GetFullPath($DataRoot)
    if (Test-Path -LiteralPath $dataRoot) {
        Write-Step "Removing the ledger at $dataRoot"
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}
else {
    Write-Host "The ledger under $DataRoot was kept. Pass -RemoveData to delete it."
}

Write-Host "Task Token Meter was removed."
