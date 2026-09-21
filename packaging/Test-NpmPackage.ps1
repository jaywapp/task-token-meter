<#
.SYNOPSIS
    Installs the packed npm tarballs into a temporary project and runs the CLI through the launcher.

.PARAMETER PackRoot
    The OutputRoot produced by Build-NpmPackages.ps1.

.PARAMETER Version
    The version the packages declare.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackRoot,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$script:failures = 0
$script:checks = 0

function Test-Condition {
    param([string]$Name, [bool]$Condition, [string]$Detail = "")
    $script:checks++
    if ($Condition) { Write-Host "  PASS  $Name" }
    else { $script:failures++; Write-Host "  FAIL  $Name $Detail" }
}

if ($Version.StartsWith("v")) { $Version = $Version.Substring(1) }
$tarballs = Get-ChildItem -LiteralPath $PackRoot -Filter "*.tgz" -File
if ($tarballs.Count -lt 2) { throw "Expected the launcher and platform tarballs in $PackRoot." }
$platformTarball = ($tarballs | Where-Object { $_.Name -like "*win32-x64*" } | Select-Object -First 1).FullName
$launcherTarball = ($tarballs | Where-Object { $_.Name -notlike "*win32-x64*" } | Select-Object -First 1).FullName
if (-not $platformTarball -or -not $launcherTarball) { throw "Could not identify both tarballs in $PackRoot." }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-npm-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    Push-Location $sandbox
    try {
        # npm.ps1 is not strict-mode clean, so the .cmd shim is used instead.
        $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
        if (-not $npm) { throw "npm.cmd was not found on PATH." }

        Write-Host "==> Installing the packed tarballs"
        & $npm.Source init -y --silent | Out-Null
        & $npm.Source install --silent --no-audit --no-fund $platformTarball $launcherTarball
        Test-Condition "npm install exit code is 0" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"

        $launcherBin = Join-Path $sandbox "node_modules\.bin\task-token-meter.cmd"
        Test-Condition "launcher bin is installed" (Test-Path -LiteralPath $launcherBin)

        Write-Host "==> Running the launcher"
        $versionOutput = & $launcherBin version --json 2>&1
        Test-Condition "launcher exit code is 0" ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
        $reported = (($versionOutput -join "") | ConvertFrom-Json).version
        Test-Condition "launcher reports the packaged version" ($reported -eq $Version) $reported

        $helpOutput = & $launcherBin --help 2>&1
        Test-Condition "launcher prints help" ((($helpOutput -join "")) -match "token-meter")

        $embedded = Join-Path $sandbox "node_modules\@jaywapp\task-token-meter-win32-x64\dist\task-token-meter.exe"
        Test-Condition "platform package ships the executable" (Test-Path -LiteralPath $embedded)
        Test-Condition "platform package ships native SQLite" (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $embedded) "e_sqlite3.dll"))

        Write-Host "==> Exit codes pass through"
        # Windows PowerShell turns native stderr into terminating errors while ErrorActionPreference is Stop.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try { & $launcherBin unknown-command > $null 2> $null }
        finally { $ErrorActionPreference = $previousPreference }
        Test-Condition "unknown command exits with 2" ($LASTEXITCODE -eq 2) "exit $LASTEXITCODE"
    }
    finally { Pop-Location }
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("Checks: " + $script:checks + ", failures: " + $script:failures)
if ($script:failures -gt 0) { exit 1 }
exit 0
