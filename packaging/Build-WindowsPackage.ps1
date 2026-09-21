param(
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-package-" + [Guid]::NewGuid().ToString("N"))),
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $outputRoot) {
    throw "OutputRoot must not already exist."
}

$publishRoot = Join-Path $outputRoot "publish"
$korean = -join ([char[]]@(0xD55C, 0xAE00))
$installRoot = Join-Path $outputRoot ("clean install " + $korean)
$archivePath = Join-Path $outputRoot "task-token-meter-win-x64.zip"
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$publishArguments = @(
    (Join-Path $repoRoot "src\TaskTokenMeter.Cli\TaskTokenMeter.Cli.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", $publishRoot,
    "-p:RestoreLockedMode=true", "-p:DebugSymbols=false", "-p:DebugType=None", "-p:PublishReadyToRun=true"
)
if ($Version) {
    if ($Version.StartsWith("v")) { $Version = $Version.Substring(1) }
    $publishArguments += ("-p:Version=" + $Version)
}
& dotnet publish @publishArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$requiredFiles = @("task-token-meter.exe", "coreclr.dll", "hostfxr.dll", "e_sqlite3.dll")
foreach ($required in $requiredFiles) {
    if (-not (Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object { $_.Name -eq $required })) {
        throw "Required self-contained or native file is missing: $required"
    }
}

$forbidden = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".pdb", ".map", ".jsonl", ".log", ".env", ".cs", ".csproj")
}
if ($forbidden) { throw "Forbidden build, source-map, source, or raw-log files were published." }

$textFiles = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".json", ".cmd", ".xml", ".config")
}
foreach ($file in $textFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($text -match '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|(?<![A-Za-z0-9])(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{16,}|xoxb-[A-Za-z0-9-]{16,})') {
        throw "Secret-like content was found in $($file.Name)."
    }
}

Copy-Item -LiteralPath $publishRoot -Destination $installRoot -Recurse
$oldDotnetRoot = $env:DOTNET_ROOT
$oldMultilevel = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path $outputRoot "runtime-does-not-exist"
    $env:DOTNET_MULTILEVEL_LOOKUP = "0"
    $help = & (Join-Path $installRoot "task-token-meter.exe") --help 2>&1
    if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -notmatch "token-meter") {
        throw "Self-contained clean-install smoke failed."
    }

    $versionJson = & (Join-Path $installRoot "task-token-meter.exe") version --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The published executable failed to report its version." }
    $reportedVersion = (($versionJson -join "") | ConvertFrom-Json).version
    if (-not $reportedVersion) { throw "The published executable reported an empty version." }
    if ($Version -and $reportedVersion -ne $Version) {
        throw "The published version $reportedVersion does not match the requested version $Version."
    }
}
finally {
    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $oldMultilevel
}

$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $outputRoot
    $claudeOutput = & $env:ComSpec /d /c ('call "' + (Join-Path $installRoot "packaging\hooks\claude\task-token-meter-hook.cmd") + '" ^<NUL')
    if ($LASTEXITCODE -ne 0 -or ($claudeOutput -join "") -ne "") { throw "Claude wrapper smoke failed." }
    $codexOutput = & $env:ComSpec /d /c ('call "' + (Join-Path $installRoot "packaging\hooks\codex\task-token-meter-hook.cmd") + '" ^<NUL')
    if ($LASTEXITCODE -ne 0 -or ($codexOutput -join "") -ne "{}") { throw "Codex wrapper smoke failed." }
}
finally {
    Set-Location -LiteralPath $originalLocation
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
$files = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path = $_.FullName.Substring($publishRoot.Length + 1).Replace("\", "/")
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [pscustomobject]@{
    schemaVersion = 1
    version = $reportedVersion
    runtimeIdentifier = "win-x64"
    selfContained = $true
    files = $files
}
$manifestPath = Join-Path $outputRoot "contents.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($archivePath + ".sha256"), ($archiveHash + "  " + [IO.Path]::GetFileName($archivePath) + "`n"), [Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination (Join-Path $outputRoot "install.ps1")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall.ps1") -Destination (Join-Path $outputRoot "uninstall.ps1")

[pscustomobject]@{
    package = $archivePath
    version = $reportedVersion
    sha256 = $archiveHash
    bytes = (Get-Item -LiteralPath $archivePath).Length
    files = $files.Count
    nativeSqlite = $true
    selfContainedSmoke = $true
    wrapperSmoke = $true
} | ConvertTo-Json
