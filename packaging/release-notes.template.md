# Task Token Meter {{VERSION}}

Windows x64 self-contained package. A separate .NET runtime installation is not required.
The executable is not code signed, so SmartScreen may warn on first run; verify the SHA-256 below.

## Install

```powershell
Invoke-WebRequest https://github.com/{{REPOSITORY}}/releases/download/{{TAG}}/install.ps1 -OutFile install-task-token-meter.ps1
Get-Content .\install-task-token-meter.ps1
.\install-task-token-meter.ps1 -Version {{VERSION}}
```

Hooks are not installed automatically. Run `task-token-meter hook install --provider claude --provider-version 2.1.278`
and `task-token-meter hook install --provider codex --provider-version 0.153.4` when you want measurement to start.

Update with the same command and a newer version; uninstall with `uninstall.ps1`, which keeps the ledger unless `-RemoveData` is passed.

## Package

| Item | Value |
|---|---|
| Archive | `task-token-meter-win-x64.zip` |
| SHA-256 | `{{SHA256}}` |
| Bytes | {{BYTES}} |
| Files | {{FILES}} |

Verify the build provenance:

```powershell
gh attestation verify .\task-token-meter-win-x64.zip --repo {{REPOSITORY}}
```

## Supported targets

| Target | Supported version |
|---|---|
| Claude Code | 2.1.278 |
| Codex CLI | 0.153.4 |
| OS | Windows x64 |

Unsupported provider schemas exit with code 5.

## Known limits

See the repository README for the current release limits, including the open performance and
session-discovery issues, and the parts that were verified with synthetic fixtures only.
