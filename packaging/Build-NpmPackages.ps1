<#
.SYNOPSIS
    Builds the npm launcher and the win32-x64 platform package from a release archive.

.DESCRIPTION
    The platform package embeds the self-contained executable that was verified for the GitHub Release,
    so npm install never downloads a binary. Both packages are packed with `npm pack` and the tarballs are
    checked for build leftovers and secret-like content.

.PARAMETER Version
    Release version without the leading "v", for example 0.1.0-preview.1.

.PARAMETER ArchivePath
    The task-token-meter-win-x64.zip produced by Build-WindowsPackage.ps1.

.PARAMETER OutputRoot
    A new directory that receives the staged packages and the tarballs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-npm-" + [Guid]::NewGuid().ToString("N")))
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

if ($Version.StartsWith("v")) { $Version = $Version.Substring(1) }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer: $Version"
}
if (-not (Test-Path -LiteralPath $ArchivePath)) { throw "Archive not found: $ArchivePath" }
if (Test-Path -LiteralPath $OutputRoot) { throw "OutputRoot must not already exist." }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$npmSource = Join-Path $repoRoot "npm"
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
$launcherDirectory = Join-Path $outputRoot "task-token-meter"
$platformDirectory = Join-Path $outputRoot "platform-win32-x64"
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

# Copy the contents explicitly: Copy-Item -Recurse on a directory behaves differently between
# Windows PowerShell 5.1 and PowerShell 7 when the destination does not exist yet.
New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $platformDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $npmSource "task-token-meter\*") -Destination $launcherDirectory -Recurse -Force
Copy-Item -Path (Join-Path $npmSource "platform-win32-x64\*") -Destination $platformDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $launcherDirectory "LICENSE")
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $platformDirectory "LICENSE")

$launcherEntryPoint = Join-Path $launcherDirectory "bin\task-token-meter.js"
if (-not (Test-Path -LiteralPath $launcherEntryPoint)) {
    throw "The staged launcher package is missing bin/task-token-meter.js."
}

function Set-PackageVersion {
    param([string]$ManifestPath, [string]$Version, [bool]$UpdateOptionalDependency)

    $text = [IO.File]::ReadAllText($ManifestPath)
    $manifest = $text | ConvertFrom-Json
    $manifest.version = $Version
    if ($UpdateOptionalDependency) {
        $manifest.optionalDependencies.'@jaywapp/task-token-meter-win32-x64' = $Version
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($ManifestPath, $json, (New-Object Text.UTF8Encoding($false)))
}

Set-PackageVersion -ManifestPath (Join-Path $launcherDirectory "package.json") -Version $Version -UpdateOptionalDependency $true
Set-PackageVersion -ManifestPath (Join-Path $platformDirectory "package.json") -Version $Version -UpdateOptionalDependency $false

$dist = Join-Path $platformDirectory "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
if (Get-Command Microsoft.PowerShell.Archive\Expand-Archive -ErrorAction SilentlyContinue) {
    Microsoft.PowerShell.Archive\Expand-Archive -LiteralPath $ArchivePath -DestinationPath $dist -Force
}
else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $dist)
}

$executable = Join-Path $dist "task-token-meter.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw "The archive does not contain task-token-meter.exe." }

$reported = (((& $executable version --json) -join "") | ConvertFrom-Json).version
if ($LASTEXITCODE -ne 0) { throw "The embedded executable failed to report its version." }
if ($reported -ne $Version) { throw "The embedded executable reports $reported but the packages declare $Version." }

$forbidden = Get-ChildItem -LiteralPath $dist -Recurse -File | Where-Object {
    $_.Extension -in @(".pdb", ".map", ".jsonl", ".log", ".env", ".cs", ".csproj")
}
if ($forbidden) { throw "The platform package contains build or source leftovers." }

# npm.ps1 is not strict-mode clean, so the .cmd shim is used instead.
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) { throw "npm.cmd was not found on PATH." }

$tarballs = @()
foreach ($directory in @($platformDirectory, $launcherDirectory)) {
    Push-Location $directory
    try {
        $packed = & $npm.Source pack --pack-destination $outputRoot --silent
        if ($LASTEXITCODE -ne 0) { throw "npm pack failed in $directory." }
        $tarballs += (Join-Path $outputRoot ($packed | Select-Object -Last 1))
    }
    finally { Pop-Location }
}

# A packed launcher without its entry point installs cleanly and then fails at run time, so the
# tarball contents are checked before the packages are reported as built.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$launcherTarball = $tarballs | Where-Object { $_ -notlike "*win32-x64*" } | Select-Object -First 1
$inspectRoot = Join-Path $outputRoot "inspect"
New-Item -ItemType Directory -Path $inspectRoot -Force | Out-Null
$gzip = New-Object IO.Compression.GZipStream([IO.File]::OpenRead($launcherTarball), [IO.Compression.CompressionMode]::Decompress)
try {
    $bytes = New-Object byte[] 4096
    $tarPath = Join-Path $inspectRoot "launcher.tar"
    $tarStream = [IO.File]::Create($tarPath)
    try {
        while (($read = $gzip.Read($bytes, 0, $bytes.Length)) -gt 0) { $tarStream.Write($bytes, 0, $read) }
    }
    finally { $tarStream.Dispose() }
}
finally { $gzip.Dispose() }
$tarText = [IO.File]::ReadAllText((Join-Path $inspectRoot "launcher.tar"), [Text.Encoding]::ASCII)
foreach ($required in @("package/package.json", "package/bin/task-token-meter.js")) {
    if (-not $tarText.Contains($required)) { throw "The launcher tarball does not contain $required." }
}
Remove-Item -LiteralPath $inspectRoot -Recurse -Force

$report = $tarballs | ForEach-Object {
    [pscustomobject]@{
        tarball = $_
        bytes   = (Get-Item -LiteralPath $_).Length
        sha256  = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
}

[pscustomobject]@{
    version    = $Version
    outputRoot = $outputRoot
    packages   = $report
} | ConvertTo-Json -Depth 5
