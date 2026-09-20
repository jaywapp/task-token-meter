param(
    [Parameter(Mandatory = $true)][string]$Executable
)

$ErrorActionPreference = "Stop"
$executablePath = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "Executable was not found." }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$source = Join-Path $repoRoot "tests\fixtures\codex\two-turn-snapshots.jsonl"
$root = Join-Path ([IO.Path]::GetTempPath()) ("task-token-meter-ps51-" + [Guid]::NewGuid().ToString("N"))
$korean = -join ([char[]]@(0xD55C, 0xAE00))
$workspace = Join-Path $root ($korean + " workspace")
$redirected = Join-Path $root "redirected.json"
$stderr = Join-Path $root "stderr.txt"
New-Item -ItemType Directory -Path $workspace -Force | Out-Null

$oldCodexSources = $env:TOKEN_METER_CODEX_SOURCES
$oldClaudeSources = $env:TOKEN_METER_CLAUDE_SOURCES
try {
    $env:TOKEN_METER_CODEX_SOURCES = $source
    $env:TOKEN_METER_CLAUDE_SOURCES = Join-Path $root "not-present"

    $pipedJson = '' | & $executablePath current --provider codex --session synthetic-codex-session-two-turn --workspace $workspace --json 2>$stderr
    if ($LASTEXITCODE -ne 0) { throw "stdin/stdout pipe smoke failed with code $LASTEXITCODE." }
    $parsedPipe = ($pipedJson -join "`n") | ConvertFrom-Json
    if ($parsedPipe.schemaVersion -ne 1 -or $parsedPipe.turns[0].processedTokens -ne 50) {
        throw "stdin/stdout pipe JSON contract failed."
    }

    & $executablePath turns --provider codex --session synthetic-codex-session-two-turn --workspace $workspace --json > $redirected 2>$stderr
    if ($LASTEXITCODE -ne 0) { throw "stdout redirection smoke failed with code $LASTEXITCODE." }
    $parsedRedirect = (Get-Content -LiteralPath $redirected -Raw) | ConvertFrom-Json
    if ($parsedRedirect.schemaVersion -ne 1 -or $parsedRedirect.turns.Count -ne 2) {
        throw "PowerShell 5.1 redirected JSON contract failed."
    }

    $selectorJson = & $executablePath current --json --workspace $workspace 2>$stderr
    if ($LASTEXITCODE -ne 4) { throw "JSON selector failure code was $LASTEXITCODE instead of 4." }
    $selectorError = ($selectorJson -join "`n") | ConvertFrom-Json
    if ($selectorError.error.code -ne "selector_required") { throw "JSON selector error was not isolated on stdout." }

    [pscustomobject]@{
        schemaVersion = 1
        powershell = $PSVersionTable.PSVersion.ToString()
        stdinPipe = "pass"
        stdoutPipe = "pass"
        fileRedirection = "pass"
        jsonSelectorExitCode = 4
        stdoutJsonOnly = "pass"
    } | ConvertTo-Json
}
finally {
    $env:TOKEN_METER_CODEX_SOURCES = $oldCodexSources
    $env:TOKEN_METER_CLAUDE_SOURCES = $oldClaudeSources
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
