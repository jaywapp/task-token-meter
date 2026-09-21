<#
.SYNOPSIS
    Installs or updates Task Token Meter for the current user on Windows x64.

.DESCRIPTION
    Downloads the release package from GitHub, verifies its SHA-256, and installs it under
    %LOCALAPPDATA%\Programs\TaskTokenMeter\versions\<version>. A version independent shim is written to
    bin\task-token-meter.cmd and that directory is added to the user PATH, so Hook entries and shell
    commands keep working after an update.

    Administrator rights are not required and the system PATH is not modified.

.PARAMETER Version
    Release version without the leading "v", for example 0.1.0-preview.1. When omitted the latest
    stable release is used. GitHub's "latest" release never resolves to a prerelease, so pass -PreRelease
    or an explicit -Version while only previews exist.

.PARAMETER PreRelease
    Installs the newest prerelease instead of the latest stable release.

.PARAMETER ArchivePath
    Installs from a local package instead of downloading. Used by the installer tests and for offline
    installs. The matching .sha256 file is honoured when it sits next to the archive.

.PARAMETER SkipPathUpdate
    Does not modify the user PATH. Used by the installer tests.

.EXAMPLE
    .\install.ps1 -Version 0.1.0-preview.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$PreRelease,
    [string]$Repository = "jaywapp/task-token-meter",
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\TaskTokenMeter"),
    [string]$ArchivePath,
    [switch]$SkipPathUpdate
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$archiveName = "task-token-meter-win-x64.zip"
$shimName = "task-token-meter.cmd"

function Write-Step { param([string]$Message) Write-Host "==> $Message" }

function Assert-SupportedPlatform {
    if ($PSVersionTable.PSVersion.Major -lt 5) {
        throw "Windows PowerShell 5.1 or later is required."
    }
    $isWindows = $true
    if ($PSVersionTable.PSEdition -eq "Core") { $isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) }
    if (-not $isWindows) {
        throw "Task Token Meter supports Windows only. Nothing was changed."
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw "Task Token Meter supports the win-x64 architecture only. Nothing was changed."
    }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($architecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
        throw "Unsupported OS architecture: $architecture. Nothing was changed."
    }
}

function Enable-Tls12 {
    try {
        $current = [Net.ServicePointManager]::SecurityProtocol
        if (($current -band [Net.SecurityProtocolType]::Tls12) -ne [Net.SecurityProtocolType]::Tls12) {
            [Net.ServicePointManager]::SecurityProtocol = $current -bor [Net.SecurityProtocolType]::Tls12
        }
    }
    catch {
        Write-Verbose "TLS 1.2 could not be enabled explicitly: $($_.Exception.Message)"
    }
}

function Get-ReleaseTag {
    param([string]$Repository, [string]$Version, [bool]$PreRelease)

    if ($Version) {
        if ($Version.StartsWith("v")) { return $Version }
        return "v" + $Version
    }

    $headers = @{ "User-Agent" = "task-token-meter-installer"; "Accept" = "application/vnd.github+json" }
    if ($PreRelease) {
        $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=20" -Headers $headers
        $candidate = $releases | Where-Object { $_.prerelease -eq $true -and $_.draft -eq $false } | Select-Object -First 1
        if (-not $candidate) { throw "No prerelease was found for $Repository." }
        return $candidate.tag_name
    }

    try {
        $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers $headers
        return $latest.tag_name
    }
    catch {
        throw "No stable release was found for $Repository. Use -PreRelease or -Version while previews are the only releases."
    }
}

function Get-VersionFromTag {
    param([string]$Tag)
    if ($Tag.StartsWith("v")) { return $Tag.Substring(1) }
    return $Tag
}

function Get-ExpectedHash {
    param([string]$ShaText)
    $token = ($ShaText -split '\s+') | Where-Object { $_ } | Select-Object -First 1
    if (-not $token -or $token.Length -ne 64) { throw "The SHA-256 file is malformed." }
    return $token.ToUpperInvariant()
}

function Resolve-Package {
    param([string]$Repository, [string]$Tag, [string]$ArchivePath, [string]$StagingRoot)

    $target = Join-Path $StagingRoot $archiveName
    if ($ArchivePath) {
        if (-not (Test-Path -LiteralPath $ArchivePath)) { throw "The archive was not found: $ArchivePath" }
        Copy-Item -LiteralPath $ArchivePath -Destination $target
        $localSha = $ArchivePath + ".sha256"
        if (Test-Path -LiteralPath $localSha) {
            return [pscustomobject]@{ Path = $target; ExpectedHash = (Get-ExpectedHash (Get-Content -LiteralPath $localSha -Raw)) }
        }
        return [pscustomobject]@{ Path = $target; ExpectedHash = $null }
    }

    $base = "https://github.com/$Repository/releases/download/$Tag"
    Write-Step "Downloading $base/$archiveName"
    Invoke-WebRequest -Uri "$base/$archiveName" -OutFile $target -UseBasicParsing
    $shaPath = $target + ".sha256"
    Invoke-WebRequest -Uri "$base/$archiveName.sha256" -OutFile $shaPath -UseBasicParsing
    return [pscustomobject]@{ Path = $target; ExpectedHash = (Get-ExpectedHash (Get-Content -LiteralPath $shaPath -Raw)) }
}

function Write-Shim {
    param([string]$BinDirectory, [string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $BinDirectory)) {
        New-Item -ItemType Directory -Path $BinDirectory -Force | Out-Null
    }
    $shimPath = Join-Path $BinDirectory $shimName
    $lines = @(
        '@echo off',
        'rem Managed by the Task Token Meter installer. Rewritten on every install.',
        'setlocal',
        ('set "TASK_TOKEN_METER_EXE=' + $ExecutablePath + '"'),
        'if not exist "%TASK_TOKEN_METER_EXE%" (',
        '  echo task-token-meter: the installed executable is missing. Reinstall Task Token Meter.>&2',
        '  exit /b 1',
        ')',
        '"%TASK_TOKEN_METER_EXE%" %*',
        'exit /b %ERRORLEVEL%'
    )
    Set-Content -LiteralPath $shimPath -Value $lines -Encoding ASCII
    return $shimPath
}

function Add-UserPath {
    param([string]$Directory)

    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not $current) { $current = "" }
    $entries = $current -split ';' | Where-Object { $_ }
    foreach ($entry in $entries) {
        if ($entry.TrimEnd('\') -ieq $Directory.TrimEnd('\')) { return $false }
    }
    $updated = (@($entries) + $Directory) -join ';'
    [Environment]::SetEnvironmentVariable("Path", $updated, "User")
    return $true
}

Assert-SupportedPlatform
Enable-Tls12

$tag = Get-ReleaseTag -Repository $Repository -Version $Version -PreRelease ([bool]$PreRelease)
$resolvedVersion = Get-VersionFromTag -Tag $tag
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$versionsRoot = Join-Path $installRoot "versions"
$binDirectory = Join-Path $installRoot "bin"
$targetVersionDirectory = Join-Path $versionsRoot $resolvedVersion
$statePath = Join-Path $installRoot "install-state.json"
$previousState = $null
if (Test-Path -LiteralPath $statePath) {
    $previousState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    $package = Resolve-Package -Repository $Repository -Tag $tag -ArchivePath $ArchivePath -StagingRoot $staging
    if ($package.ExpectedHash) {
        Write-Step "Verifying SHA-256"
        $actual = (Get-FileHash -LiteralPath $package.Path -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $package.ExpectedHash) {
            throw "The package hash does not match the published value. Nothing was installed."
        }
    }
    else {
        Write-Warning "No .sha256 file was available for this package; the hash was not verified."
    }

    $extracted = Join-Path $staging "extracted"
    New-Item -ItemType Directory -Path $extracted -Force | Out-Null
    Write-Step "Extracting the package"
    if (Get-Command Microsoft.PowerShell.Archive\Expand-Archive -ErrorAction SilentlyContinue) {
        Microsoft.PowerShell.Archive\Expand-Archive -LiteralPath $package.Path -DestinationPath $extracted -Force
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($package.Path, $extracted)
    }

    $stagedExecutable = Join-Path $extracted "task-token-meter.exe"
    if (-not (Test-Path -LiteralPath $stagedExecutable)) {
        throw "The package does not contain task-token-meter.exe."
    }

    Write-Step "Verifying the extracted executable"
    $reportedVersion = & $stagedExecutable version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The extracted executable failed to report its version." }

    New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null
    if (Test-Path -LiteralPath $targetVersionDirectory) {
        $retired = $targetVersionDirectory + ".replaced-" + [Guid]::NewGuid().ToString("N")
        Move-Item -LiteralPath $targetVersionDirectory -Destination $retired
        Remove-Item -LiteralPath $retired -Recurse -Force -ErrorAction SilentlyContinue
    }
    Move-Item -LiteralPath $extracted -Destination $targetVersionDirectory

    $installedExecutable = Join-Path $targetVersionDirectory "task-token-meter.exe"
    $shimPath = Write-Shim -BinDirectory $binDirectory -ExecutablePath $installedExecutable

    Write-Step "Running the installed executable"
    $versionOutput = & $shimPath version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The installed executable failed to run through the shim." }
    $helpOutput = & $shimPath --help 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The installed executable failed to print help." }

    $pathChanged = $false
    if (-not $SkipPathUpdate) {
        $pathChanged = Add-UserPath -Directory $binDirectory
        if ($env:Path -notlike "*$binDirectory*") { $env:Path = $env:Path + ";" + $binDirectory }
    }

    $state = [pscustomobject]@{
        schemaVersion    = 1
        version          = $resolvedVersion
        tag              = $tag
        repository       = $Repository
        installRoot      = $installRoot
        versionDirectory = $targetVersionDirectory
        executablePath   = $installedExecutable
        shimPath         = $shimPath
        pathEntry        = $binDirectory
        pathManaged      = (-not $SkipPathUpdate)
        installedAtUtc   = [DateTime]::UtcNow.ToString("o")
    }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))

    if ($previousState -and $previousState.version -ne $resolvedVersion) {
        $previousDirectory = Join-Path $versionsRoot $previousState.version
        if (Test-Path -LiteralPath $previousDirectory) {
            Write-Step "Removing the previous version $($previousState.version)"
            Remove-Item -LiteralPath $previousDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host ""
    Write-Host "Task Token Meter $resolvedVersion is installed."
    Write-Host "  executable : $installedExecutable"
    Write-Host "  command    : $shimPath"
    if ($pathChanged) { Write-Host "  PATH       : $binDirectory was added; open a new terminal to use 'task-token-meter'." }
    Write-Host ""
    Write-Host "Hooks are not installed automatically. Run the provider commands when you want measurement to start:"
    Write-Host "  task-token-meter hook install --provider claude --provider-version 2.1.278"
    Write-Host "  task-token-meter hook install --provider codex --provider-version 0.153.4"
    ($versionOutput | Select-Object -First 1)
}
catch {
    if ($previousState -and (Test-Path -LiteralPath (Join-Path $versionsRoot $previousState.version))) {
        Write-Warning "The install failed; the previously installed version $($previousState.version) is still active."
        Write-Shim -BinDirectory $binDirectory -ExecutablePath $previousState.executablePath | Out-Null
    }
    throw
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
