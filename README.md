# Task Token Meter

Task Token Meter는 Claude Code와 Codex CLI의 로컬 JSONL 사용량을 root Turn 단위로 집계하는 Windows CLI다. prompt, message, tool argument 본문을 출력하거나 저장하지 않고 token usage, 귀속 상태, 품질, source fingerprint만 다룬다.

> **Release acceptance: Fail.** 기능·내구성·패키징·성능 검증은 완료했다. `PERF-001`은 해소되어 공식 21 MiB/100,000행 fixture의 warm p95가 821.47 ms(목표 1,000 ms), peak RSS max가 89.02 MiB(목표 256 MiB)다. 남은 Fail 사유는 자동 session discovery가 workspace를 필터하지 않아 FR-04/FR-05가 Partial인 점(`SCOPE-001`) 하나이며, 그 전에는 release 기준을 통과한 것으로 간주하지 않는다.

## 지원 범위

| 대상 | 지원 기준 | 검증 상태 |
|---|---|---|
| Windows | win-x64 self-contained package | Windows 10.0.26200에서 Pass |
| Claude Code | 2.1.278 transcript, Stop/SubagentStop/StopFailure Hook | 합성 E2E Pass; 2026-09-22 실제 transcript·Hook 설치 Pass |
| Codex CLI | 0.153.4 rollout, Stop/SubagentStop/Interrupt Hook | 합성 E2E Pass; 2026-09-22 실제 rollout·Hook 설치 Pass |
| PowerShell | Windows PowerShell 5.1, PowerShell 7 | 5.1 pipe/redirect/JSON smoke와 7.x build Pass |
| .NET | package 실행에는 별도 runtime 불필요 | SDK 10.0.400/runtime 10.0.11로 build 검증 |

다른 Provider 버전, 다른 RID, network filesystem ledger는 지원 검증 대상이 아니다. 지원하지 않는 schema는 exit code 5로 끝난다.

## 설치

GitHub Release의 Windows 패키지를 설치 스크립트로 내려받는다. 관리자 권한과 .NET 설치는 필요 없다. 스크립트를 먼저 읽어 보고 실행하는 방식을 권장한다.

~~~powershell
Invoke-WebRequest `
  https://github.com/jaywapp/task-token-meter/releases/download/v0.1.0-preview.1/install.ps1 `
  -OutFile install-task-token-meter.ps1

Get-Content .\install-task-token-meter.ps1
.\install-task-token-meter.ps1 -Version 0.1.0-preview.1
~~~

안정판이 나온 뒤에는 버전을 생략하면 최신 안정판이 설치된다. GitHub의 `latest`는 prerelease를 가리키지 않으므로, preview만 있는 동안에는 `-Version`이나 `-PreRelease`를 지정해야 한다.

~~~powershell
.\install-task-token-meter.ps1              # 최신 안정판
.\install-task-token-meter.ps1 -PreRelease  # 최신 preview
~~~

설치 결과는 다음과 같다.

| 경로 | 내용 |
|---|---|
| `%LOCALAPPDATA%\Programs\TaskTokenMeter\versions\<version>` | 버전별 실행 파일과 runtime |
| `%LOCALAPPDATA%\Programs\TaskTokenMeter\bin\task-token-meter.cmd` | 버전과 무관한 고정 명령. 사용자 PATH에 추가된다 |
| `%LOCALAPPDATA%\Programs\TaskTokenMeter\install-state.json` | 활성 버전과 설치 기록 |

업데이트는 같은 명령을 새 버전으로 다시 실행한다. 새 버전을 별도 디렉터리에 설치하고 검증한 뒤 고정 명령이 가리키는 대상을 바꾸며, 실패하면 이전 버전이 그대로 활성 상태로 남는다. Hook 항목은 버전 디렉터리가 아니라 고정 명령을 기록하므로 업데이트 후에도 유효하다.

새 terminal에서 다음을 확인한다.

~~~powershell
task-token-meter --version
task-token-meter --help
~~~

### 저장소에서 직접 빌드

~~~powershell
dotnet restore TaskTokenMeter.sln --locked-mode
$outputRoot = Join-Path $env:TEMP ("task-token-meter-" + [guid]::NewGuid().ToString("N"))
.\packaging\Build-WindowsPackage.ps1 -OutputRoot $outputRoot -Version 0.1.0-preview.1
.\packaging\install.ps1 -Version 0.1.0-preview.1 -ArchivePath (Join-Path $outputRoot "task-token-meter-win-x64.zip")
~~~

패키지 스크립트는 win-x64, self-contained, ReadyToRun 실행 파일과 Hook wrapper를 만들고 native SQLite, runtime 없는 clean-install 실행, 공백·한글 경로 wrapper를 검사한다. `packaging/Test-Installer.ps1`은 설치·업데이트·실패 복구·제거와 Hook 경로 유지를 임시 디렉터리에서 검증한다.

## 빠른 시작

현재 Git workspace는 `--workspace`를 생략하면 현재 디렉터리부터 위로 올라가며 가장 가까운 `.git`을 찾는다. 현재 구현의 자동 세션 후보는 workspace로 걸러지지 않으므로, 안전하고 재현 가능한 조회에는 `--provider`와 `--session`을 함께 지정한다.

~~~powershell
task-token-meter current --provider codex --session "<session-id>" --workspace .
task-token-meter last --provider codex --session "<session-id>" --workspace .
task-token-meter turns --provider codex --session "<session-id>" --workspace . --json
task-token-meter sync --provider codex --session "<session-id>" --workspace .
~~~

- `current`: 관측 시각이 가장 최근인 Turn 하나를 표시한다.
- `last`: completed, interrupted, failed 중 가장 최근 terminal Turn을 표시한다. terminal Turn이 없으면 최신 Turn을 provisional로 표시한다.
- `turns`: 선택한 session의 Turn 전체를 시간순으로 표시한다.
- `sync`: source를 다시 읽고 활성 ledger에 revision과 source manifest를 함께 반영한다.
- `rebuild`: 선택 session을 source에서 다시 계산해 활성 ledger에 반영한다. source가 없으면 저장된 projection을 provisional fallback으로 읽는다.
- `version`(`--version`): 설치된 버전을 표시한다. `--json`은 `version`과 `fileVersion`을 함께 출력한다.

`--json`은 stdout에 JSON 객체 하나만 쓴다. `--strict`는 결과에 observed가 아닌 measurement가 하나라도 있으면 결과를 출력한 뒤 exit code 6을 반환한다. session 후보가 여러 개이고 실제 대화형 console이면 번호를 선택할 수 있다. CI, pipe, redirect, `--json`, `--non-interactive`에서는 선택을 기다리지 않고 code 4를 반환한다.

Source root 기본값은 `%USERPROFILE%\.claude\projects`와 `%USERPROFILE%\.codex\sessions`다. 합성 fixture나 별도 root를 쓸 때는 path separator(`;`)로 구분한다.

~~~powershell
$env:TOKEN_METER_CLAUDE_SOURCES = "D:\safe-fixtures\claude"
$env:TOKEN_METER_CODEX_SOURCES = "D:\safe-fixtures\codex"
~~~

## 저장과 복구

기본 모드는 global이다. global data root는 `%LOCALAPPDATA%\TaskTokenMeter`이고, ledger는 그 아래 `ledger.db`, route registry와 lock은 `state`, migration journal/backup은 `state\migration` 아래에 둔다. workspace 모드는 `<workspace>\.token-meter\ledger.db`를 사용하고 Git exclude를 먼저 확인한다.

~~~powershell
task-token-meter storage status --workspace . --json
task-token-meter storage migrate --workspace . --to workspace --dry-run --json
task-token-meter storage migrate --workspace . --to workspace --json
task-token-meter storage migrate --workspace . --to global --json
~~~

Migration은 workspace 단위 writer lock, source backup, manifest 검증, destination import, route switch, journal 완료 순서로 실행된다. source가 route switch 전에 바뀌면 이전 route를 유지하고 code 7로 실패한다. 같은 명령을 다시 실행하면 journal endpoint와 generation을 검증하고 현재 source로 계획을 다시 만든다. crash 뒤에도 완료된 단계와 route를 관찰해 재시도하며, 독립 destination 데이터가 있으면 덮어쓰지 않는다.

## Hook

설치는 지원 Provider 버전을 명시해야 한다. 기본 설정 파일은 Claude `%USERPROFILE%\.claude\settings.json`, Codex `%USERPROFILE%\.codex\hooks.json`이다. 설치는 기존 설정을 보존하고 `.task-token-meter.bak` backup을 만든 뒤 관리 entry만 추가한다.

~~~powershell
task-token-meter hook install --provider claude --provider-version 2.1.278 --json
task-token-meter hook status --provider claude --json
task-token-meter hook uninstall --provider claude --json

task-token-meter hook install --provider codex --provider-version 0.153.4 --json
task-token-meter hook status --provider codex --json
task-token-meter hook uninstall --provider codex --json
~~~

설치 시 기록하는 실행 경로는 버전 디렉터리가 아니라 설치기가 만든 고정 명령(`bin\task-token-meter.cmd`)이다. 따라서 업데이트로 이전 버전 디렉터리가 사라져도 Hook entry는 유효하다. `hook status`는 기록된 실행 파일 경로와 존재 여부(`executableAvailable`)를 함께 보고하므로, 설치를 통째로 옮기거나 지운 경우를 조용한 무동작 대신 확인할 수 있다. `TOKEN_METER_HOOK_EXECUTABLE`로 기록할 경로를 직접 지정할 수도 있다.

다른 설정 파일이나 실행 파일을 시험할 때는 absolute JSON `--settings`와 absolute `--executable`을 지정한다. Hook entrypoint는 payload 크기, event, ID, workspace, `.jsonl` transcript, Provider source root의 물리 경로를 검증한다. junction/symlink가 source root 밖을 가리키면 거부한다. Claude는 빈 stdout, Codex Stop/SubagentStop은 `{}`, Interrupt는 빈 stdout으로 즉시 neutral 응답하고 별도 worker를 시작한다. malformed payload, process 시작 실패, timeout, aggregation 실패는 Provider 실행을 막지 않으며 본문 없는 진단 code만 기록한다.

## 데이터와 개인정보

로컬 ledger에는 workspace ID, Provider, root session/Turn ID, token usage, execution/measurement/scope, membership identity, source availability, revision, timestamp, 진단 code가 들어간다. Source table에는 path에서 만든 opaque SHA-256 ID, content fingerprint, read extent, generation이 들어가며 원본 경로와 JSONL body는 저장하지 않는다.

Route registry와 migration journal에는 복구를 위해 canonical workspace/data root, database/backup 위치, generation, manifest hash가 들어간다. `storage status`는 database path를 사용자 terminal에 표시한다. Hook worker process argument에는 검증된 session/Turn/workspace/transcript path가 일시적으로 전달되지만 diagnostics에는 path, ID, payload 본문을 기록하지 않는다.

다음 항목은 DB, diagnostics, 기본 text/JSON 출력, 문서, package에 저장하지 않는다.

- raw prompt와 message
- tool input/argument와 assistant text
- 환경 변수 값
- credential, token, API key
- 원본 JSONL line/body

Session/Turn ID와 token 수치는 CLI 결과에 의도적으로 표시된다. 민감한 공유 환경에서는 `--json` 결과와 `storage status` 출력도 사용자 데이터로 취급한다.

## 제거

먼저 두 Provider의 관리 Hook entry를 제거한 뒤 설치 디렉터리를 삭제한다.

~~~powershell
task-token-meter hook uninstall --provider claude
task-token-meter hook uninstall --provider codex

Invoke-WebRequest `
  https://github.com/jaywapp/task-token-meter/releases/download/v0.1.0-preview.1/uninstall.ps1 `
  -OutFile uninstall-task-token-meter.ps1
.\uninstall-task-token-meter.ps1
~~~

제거 스크립트는 설치 디렉터리와 자신이 추가한 PATH 항목만 지운다. Hook entry가 남아 있으면 중단하고 먼저 제거하라고 알린다(`-Force`로 건너뛸 수 있다). ledger는 기본적으로 보존하며 `-RemoveData`를 줄 때만 global data root를 지운다.

데이터도 제거하려면 필요한 백업을 만든 뒤 global `%LOCALAPPDATA%\TaskTokenMeter`, 각 workspace의 `.token-meter`, Provider 설정 옆의 `.task-token-meter.bak`을 사용자가 직접 삭제한다. Workspace mode를 쓰는 중이면 먼저 global로 migration하거나 해당 workspace 데이터가 더 이상 필요 없는지 확인한다.

## 종료 코드

| Code | 의미 |
|---:|---|
| 0 | 성공; Hook run은 fail-open 처리도 0 |
| 1 | 일반 명령 또는 Hook 명령 실패 |
| 2 | 잘못된 인자 |
| 3 | session/Turn 데이터 없음 |
| 4 | 비대화형 환경에서 명시적 selector 필요 |
| 5 | 지원하지 않는 source schema |
| 6 | `--strict` measurement 기준 미달 |
| 7 | storage status/migration 실패 또는 conflict |
| 130 | 사용자 취소 또는 cancellation |

## 알려진 제한

- `PERF-001` (해소): Codex adapter를 streaming projection으로 바꿔 공식 21 MiB/100,000행 warm p95가 1,648.19 ms에서 821.47 ms로, peak RSS max가 122.55 MiB에서 89.02 MiB로 내려갔다. CLI `current` 한 번이 아직 source를 세 번 읽는 여지는 [performance.md](docs/validation/performance.md)에 남겼다.
- `H-004`/`H-005` (해소, 2026-09-22): 실제 머신에 설치해 실제 Provider 로그로 처음 검증하면서 두 건을 발견했다 — `--provider`가 다른 Provider의 소스 읽기를 막지 못해 한쪽 Provider의 실제 로그 문제가 다른 쪽 조회까지 막았고(H-004), Codex CLI 0.153.4의 실제 rollout이 쓰는 `token_usage_record`의 root-level 형태를 파서가 인식하지 못해 실제 Codex usage가 0% 측정됐다(H-005). 근거·재현·수정은 [review.md](docs/validation/review.md)에 있다.
- `SCOPE-001` (Medium): 자동 discovery가 `--workspace`를 입력받지 않아 모든 configured source root의 session을 후보로 만든다. 완료 조건은 Provider metadata에서 canonical workspace identity를 얻고 discovery 단계에서 필터하며, 명시 session mismatch와 interactive 0/1/N 회귀 테스트를 추가하는 것이다. 그 전에는 `--provider`와 `--session`을 함께 쓴다.
- `CONSISTENCY-001` (Medium): adapter parse 뒤 source fingerprint/extent를 별도 read하므로 그 사이 append가 발생하면 한 번의 sync에서 projection과 manifest 시점이 달라질 수 있다. 완료 조건은 동일한 immutable snapshot/extent로 parse와 hash를 만들거나 변경을 감지해 재시도하고 append/truncate/partial-tail 회귀 테스트를 통과하는 것이다.
- 자동화 host 밖 actual interactive TTY는 Not Run이다. 실제 로그 검증은 한 대의 Windows 머신 기준이며 다른 workspace 구성·오래된 세션 형식까지 전부 확인한 것은 아니다.

## 검증 재현

~~~powershell
dotnet restore TaskTokenMeter.sln --locked-mode
dotnet restore packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj --locked-mode
dotnet build TaskTokenMeter.sln -c Release --no-restore
dotnet test TaskTokenMeter.sln -c Release --no-build
dotnet format TaskTokenMeter.sln --verify-no-changes --no-restore
dotnet list TaskTokenMeter.sln package --vulnerable --include-transitive

powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging/Smoke-PowerShell51.ps1 -Executable "<published-exe>"
.\packaging\Build-WindowsPackage.ps1 -OutputRoot "<new-empty-output-root>"
git diff --check
~~~

성능 fixture와 30회 측정 절차는 [성능 검증](docs/validation/performance.md), 전체 수용 판정은 [acceptance](docs/validation/acceptance.md), TASK-014 issue와 scan 증거는 [review](docs/validation/review.md)에 있다. 배포 채널과 release gate는 [배포 계획](docs/prepare/publish-plan.md)에 있다.

## 라이선스

[MIT](LICENSE).
