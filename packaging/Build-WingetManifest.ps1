<#
.SYNOPSIS
    Generates the WinGet community manifests for a published stable release.

.DESCRIPTION
    Reads the published release asset hash and fills the manifest templates. WinGet community
    submissions accept stable versions only, so prerelease versions are rejected.

    The generated files are written to manifests/j/Jaywapp/TaskTokenMeter/<version> so the directory can
    be copied into a fork of microsoft/winget-pkgs. Submission and Windows Sandbox validation stay manual.

.PARAMETER Version
    The published stable version, for example 0.1.0.

.PARAMETER Sha256
    The release archive SHA-256. When omitted the published .sha256 asset is downloaded.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repository = "jaywapp/task-token-meter",
    [string]$Sha256,
    [string]$ReleaseDate,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-winget-" + [Guid]::NewGuid().ToString("N")))
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

if ($Version.StartsWith("v")) { $Version = $Version.Substring(1) }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "WinGet submissions require a stable SemVer version. Got: $Version"
}

$tag = "v" + $Version
if (-not $Sha256) {
    $url = "https://github.com/$Repository/releases/download/$tag/task-token-meter-win-x64.zip.sha256"
    Write-Host "==> Downloading $url"
    $shaText = (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
    $Sha256 = ($shaText -split '\s+') | Where-Object { $_ } | Select-Object -First 1
}
$Sha256 = $Sha256.ToUpperInvariant()
if ($Sha256.Length -ne 64) { throw "The SHA-256 value is malformed." }
if (-not $ReleaseDate) { $ReleaseDate = [DateTime]::UtcNow.ToString("yyyy-MM-dd") }

$templateRoot = Join-Path $PSScriptRoot "winget"
$manifestDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ("manifests\j\Jaywapp\TaskTokenMeter\" + $Version)
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null

$generated = @()
foreach ($template in (Get-ChildItem -LiteralPath $templateRoot -Filter "*.template.yaml" -File)) {
    $text = [IO.File]::ReadAllText($template.FullName)
    $text = $text.Replace("{{VERSION}}", $Version)
    $text = $text.Replace("{{TAG}}", $tag)
    $text = $text.Replace("{{REPOSITORY}}", $Repository)
    $text = $text.Replace("{{SHA256}}", $Sha256)
    $text = $text.Replace("{{RELEASE_DATE}}", $ReleaseDate)
    if ($text.Contains("{{")) { throw "The manifest $($template.Name) still contains a placeholder." }
    $name = $template.Name.Replace(".template.yaml", ".yaml")
    $destination = Join-Path $manifestDirectory $name
    [IO.File]::WriteAllText($destination, $text, (New-Object Text.UTF8Encoding($false)))
    $generated += $destination
}

[pscustomobject]@{
    version    = $Version
    tag        = $tag
    sha256     = $Sha256
    directory  = $manifestDirectory
    manifests  = $generated
    nextSteps  = @(
        "winget validate --manifest `"$manifestDirectory`"",
        "winget install --manifest `"$manifestDirectory`"  # verify in Windows Sandbox",
        "copy the directory into a fork of microsoft/winget-pkgs and open a pull request"
    )
} | ConvertTo-Json -Depth 5
