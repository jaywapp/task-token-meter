# Task Token Meter

[![CI](https://github.com/jaywapp/task-token-meter/actions/workflows/ci.yml/badge.svg)](https://github.com/jaywapp/task-token-meter/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/jaywapp/task-token-meter?include_prereleases&label=release&color=blue)](https://github.com/jaywapp/task-token-meter/releases)
[![License: MIT](https://img.shields.io/github/license/jaywapp/task-token-meter)](LICENSE)
![Status: Preview](https://img.shields.io/badge/status-preview-orange)

**Claude Code와 Codex CLI를 하나의 세션에서 오래 쓸 때, "이번에 끝낸 작업 하나가 토큰을 얼마나 썼는지"를 바로 알려주는 Windows CLI다.**

세션 전체 누적치가 아니라 실행(root Turn) 단위로 집계한다. 로컬에 이미 있는 사용 기록(JSONL)을 읽을 뿐 API를 호출하지 않고, prompt·message·tool 인자 본문은 출력하거나 저장하지 않는다.

```powershell
task-token-meter current --provider codex --session <session-id>
```

```text
Turn          <turn-id>
Execution     completed
Measurement   observed / scope: mainOnly
Observed at   2026-09-22T09:14:02.000+00:00
Fresh input   1,240
Input (total) 48,600
Cache read    47,360
Cache write   0
Output        890
Reasoning     310
Processed     49,490
API calls     3
```

## 설치

관리자 권한과 별도 .NET 설치가 필요 없다. 아래 두 줄은 항상 최신 설치 스크립트를 내려받으므로 버전이 올라가도 그대로 써도 된다.

```powershell
Invoke-WebRequest `
  https://raw.githubusercontent.com/jaywapp/task-token-meter/main/packaging/install.ps1 `
  -OutFile install-task-token-meter.ps1

# 실행 전에 스크립트 내용을 확인하고 싶다면
Get-Content .\install-task-token-meter.ps1

.\install-task-token-meter.ps1 -PreRelease
```

> 지금은 preview만 배포 중이라 `-PreRelease`가 필요하다. 안정판(stable)이 나오면 옵션 없이 실행하는 것만으로 최신 안정판이 설치된다.

설치 후 **새 terminal**에서 확인한다.

```powershell
task-token-meter --version
task-token-meter --help
```

업데이트는 같은 명령을 다시 실행하면 된다. 새 버전을 별도 디렉터리에 설치·검증한 뒤에만 활성 버전을 바꾸므로, 설치가 실패해도 기존 버전은 그대로 남는다. `task-token-meter` 명령과 등록해 둔 Hook은 특정 버전 디렉터리가 아니라 고정된 경로를 가리키므로 업데이트 후에도 그대로 동작한다.

<details>
<summary>설치 위치 / 제거 / 저장소에서 직접 빌드</summary>

### 설치 위치

| 경로 | 내용 |
|---|---|
| `%LOCALAPPDATA%\Programs\TaskTokenMeter\versions\<version>` | 버전별 실행 파일과 runtime |
| `%LOCALAPPDATA%\Programs\TaskTokenMeter\bin\task-token-meter.cmd` | 버전과 무관한 고정 명령. 사용자 PATH에 추가된다 |
| `%LOCALAPPDATA%\TaskTokenMeter` | 기본(global) 저장 위치의 ledger |

### 제거

```powershell
task-token-meter hook uninstall --provider claude
task-token-meter hook uninstall --provider codex

Invoke-WebRequest `
  https://raw.githubusercontent.com/jaywapp/task-token-meter/main/packaging/uninstall.ps1 `
  -OutFile uninstall-task-token-meter.ps1
.\uninstall-task-token-meter.ps1
```

설치 디렉터리와 스크립트가 추가한 PATH 항목만 지운다. Hook이 아직 등록돼 있으면 먼저 제거하라고 알리고 중단한다(`-Force`로 건너뛸 수 있다). 사용 기록(ledger)은 기본적으로 남기며, `-RemoveData`를 줄 때만 `%LOCALAPPDATA%\TaskTokenMeter`까지 지운다. workspace 저장 모드를 쓰고 있었다면 `<workspace>\.token-meter`도 필요하면 따로 지운다.

### 저장소에서 직접 빌드

```powershell
git clone https://github.com/jaywapp/task-token-meter.git
cd task-token-meter
dotnet restore TaskTokenMeter.sln --locked-mode

$outputRoot = Join-Path $env:TEMP ("task-token-meter-" + [guid]::NewGuid().ToString("N"))
.\packaging\Build-WindowsPackage.ps1 -OutputRoot $outputRoot -Version 0.0.0-local
.\packaging\install.ps1 -Version 0.0.0-local -ArchivePath (Join-Path $outputRoot "task-token-meter-win-x64.zip")
```

</details>

## 지원 범위

| 대상 | 지원 기준 |
|---|---|
| OS | Windows 10/11 x64 |
| Claude Code | 2.1.278 |
| Codex CLI | 0.153.4 |
| PowerShell | Windows PowerShell 5.1, PowerShell 7 |
| .NET | 실행에는 필요 없음(self-contained). 빌드에는 SDK 10.0.400 |

다른 버전의 Claude Code·Codex나 win-x64가 아닌 환경은 아직 검증 대상이 아니다. 지원하지 않는 로그 형식을 만나면 조용히 넘어가지 않고 exit code 5로 알린다.

## 시작하기

```powershell
task-token-meter current --provider codex --session "<session-id>"   # 가장 최근 Turn
task-token-meter last    --provider codex --session "<session-id>"   # 가장 최근에 끝난 Turn
task-token-meter turns   --provider codex --session "<session-id>" --json  # 전체 Turn 목록
```

- `current`: 관측 시각이 가장 최근인 Turn 하나.
- `last`: completed·interrupted·failed 중 가장 최근에 끝난 Turn. 아직 끝난 Turn이 없으면 최신 Turn을 provisional로 표시한다.
- `turns`: 선택한 session의 Turn 전체를 시간순으로.
- `sync` / `rebuild`: 로그를 다시 읽어 로컬 ledger에 반영한다.

`--json`을 주면 stdout에 JSON 객체 하나만 쓴다. `--strict`는 실측(observed)이 아닌 값이 하나라도 섞여 있으면 정상 출력 뒤 exit code 6으로 알린다.

세션을 여러 개 조회할 수 있는 상황에서 `--session`을 생략하면, 실제 터미널에서는 번호로 고를 수 있고 CI·파이프·`--json` 환경에서는 기다리지 않고 exit code 4를 반환한다. 후보는 현재 workspace(생략 시 가장 가까운 Git root)와 일치하는 세션으로 자동 좁혀지며, workspace를 알 수 없는 세션은 숨기지 않고 후보에 남는다. `--workspace`로 명시한 session이 다른 workspace 기록으로 확인되면 자동으로 받아들이지 않는다.

## Hook으로 자동 측정하기

작업이 끝날 때마다 직접 명령을 치지 않아도 되도록, Claude Code와 Codex의 Hook에 등록해 둘 수 있다. 기존 Hook 설정은 그대로 두고 항목만 추가한다(자동 backup 생성).

```powershell
task-token-meter hook install --provider claude --provider-version 2.1.278
task-token-meter hook install --provider codex  --provider-version 0.153.4

task-token-meter hook status --provider claude
```

Hook은 검증된 payload만 받아들이고, 실제 집계는 별도 백그라운드 worker가 처리한다. worker가 실패·timeout 되어도 Claude Code·Codex의 작업 자체를 막지 않는다(fail-open). `hook status`는 등록된 실행 파일이 실제로 있는지(`executableAvailable`)도 같이 보여주므로, 설치를 옮기거나 지운 경우를 조용한 무동작 대신 바로 확인할 수 있다.

## 데이터와 개인정보

로컬 ledger에는 workspace ID, Provider, session/Turn ID, 토큰 수치, 관측 상태, source의 opaque 지문(SHA-256)만 들어간다. 다음은 어떤 경우에도 저장하거나 출력하지 않는다.

- 원본 prompt·message·tool 인자
- 원본 로그 라인 자체
- 환경 변수 값, credential, API key

모든 데이터는 이 PC를 벗어나지 않는다. 외부 서버로 전송되지 않는다.

## 저장 위치와 이동

기본은 global 저장(`%LOCALAPPDATA%\TaskTokenMeter`)이며, workspace마다 따로 저장하고 싶으면 전환할 수 있다.

```powershell
task-token-meter storage status --workspace .
task-token-meter storage migrate --workspace . --to workspace --dry-run
task-token-meter storage migrate --workspace . --to workspace
```

전환은 백업 → 검증 → 실제 전환 순서로 진행되고, 중간에 실패해도 기존 저장 위치가 그대로 유지된다(두 곳에 동시에 쓰지 않는다). 내부 schema와 동시성 설계는 [docs/storage.md](docs/storage.md)에 있다.

## 종료 코드

| Code | 의미 |
|---:|---|
| 0 | 성공 (Hook 실행은 실패해도 항상 0) |
| 1 | 일반 오류 |
| 2 | 잘못된 인자 |
| 3 | 해당 session/Turn 데이터 없음 |
| 4 | 비대화형 환경에서 `--provider`/`--session` 미지정 |
| 5 | 지원하지 않는 로그 형식 |
| 6 | `--strict` 기준 미달 |
| 7 | 저장 위치 조회/전환 실패 |
| 130 | 사용자 취소 |

## 현재 상태

> 아직 **preview**로만 배포한다. 기능·성능·패키징 검증은 끝났고, 2026-09-22 기준 [수용 판정](docs/validation/acceptance.md)은 **Pass**다 — 이 PC에서 실제 Claude Code·Codex 로그로 확인하는 과정에서 심각한 버그 두 건(H-004, H-005)과 workspace 필터 부재(SCOPE-001)를 발견해 모두 고쳤다. 안정판(stable) 태그는 아래 두 항목이 남아 있어 아직 올리지 않았다.

알려진 제한:

- 로그를 읽는 시점과 "이 파일이 최신인지" 확인하는 시점이 완전히 같은 순간은 아니다. 그 사이 로그가 이어 써지면 다음 `sync`에서 바로잡히지만, 아주 드물게 한 번의 조회 결과가 최신 상태를 완전히 반영하지 못할 수 있다(`CONSISTENCY-001`, release는 막지 않음).
- 이 정도 세부 항목까지 포함한 전체 목록은 [docs/validation/review.md](docs/validation/review.md)에 있다.

### 정식(stable) 버전까지 남은 것

| 항목 | 상태 |
|---|---|
| 성능 목표 (warm p95 ≤ 1,000 ms, peak RSS ≤ 256 MiB) | ✅ 완료 (821 ms / 89 MiB) |
| 실제 Claude Code·Codex 로그로 검증 | ✅ 완료 (2026-09-22) |
| 설치기 clean install / upgrade / rollback / uninstall 테스트 | ✅ 완료 (CI에 포함) |
| 라이선스 명시 | ✅ 완료 (MIT) |
| 세션 자동탐색의 workspace 필터링 | ✅ 완료 (2026-09-22, `SCOPE-001`) |
| Windows 코드 서명 여부 결정 | ⬜ 남음 (지금은 미서명, 안정판 전 재검토 예정) |
| 패키지 지원 정책 문서화 | ⬜ 남음 |

## 검증 재현 / 기여

```powershell
dotnet test TaskTokenMeter.sln -c Release
.\packaging\Build-WindowsPackage.ps1 -OutputRoot "<빈 새 디렉터리>"
.\packaging\Test-Installer.ps1
```

- 성능 측정 방법과 수치: [docs/validation/performance.md](docs/validation/performance.md)
- 전체 수용 판정: [docs/validation/acceptance.md](docs/validation/acceptance.md)
- 리뷰에서 발견·수정한 이슈 이력: [docs/validation/review.md](docs/validation/review.md)
- 배포 채널과 release 절차: [docs/prepare/publish-plan.md](docs/prepare/publish-plan.md)
- Hook·저장소 내부 설계: [docs/hooks.md](docs/hooks.md), [docs/storage.md](docs/storage.md)

버그 제보나 개선 제안은 [Issues](https://github.com/jaywapp/task-token-meter/issues)에 남겨 달라.

## 라이선스

[MIT](LICENSE)
