# Task Token Meter 배포 계획

작성일: 2026-09-21  
상태: Proposed — 구현 및 외부 게시 전

## 목적

사용자가 저장소를 clone하거나 .NET SDK를 설치하지 않고 Task Token Meter를 설치·업데이트·제거할 수 있는 배포 흐름을 정의한다. 현재 제품 지원 범위인 Windows x64를 우선하며, GitHub Release를 배포 산출물의 단일 원본으로 사용한다.

현재 release acceptance는 `PERF-001` 때문에 Fail이다. 따라서 최초 외부 배포는 안정판이 아니라 `v0.1.0-preview.1`과 같은 prerelease로 제한한다. 안정판은 [acceptance](../validation/acceptance.md)의 release blocker가 해소된 뒤 게시한다.

## 결정 요약

배포 채널은 다음 순서로 구축한다.

1. GitHub Release를 네이티브 바이너리와 검증 자료의 단일 원본으로 만든다.
2. PowerShell 설치기를 Windows 사용자의 기본 설치 경로로 제공한다.
3. npm은 AI CLI 사용자를 위한 보조 채널로 제공한다.
4. 안정판 이후 WinGet manifest를 제출한다.

GitHub Release의 바이너리를 각 채널이 다시 빌드하지 않는다. Release workflow에서 한 번 만든 산출물을 PowerShell 설치기, npm 패키지와 WinGet manifest가 동일한 버전·해시로 참조해야 한다.

## 현재 상태

### 준비된 항목

- `packaging/Build-WindowsPackage.ps1`이 win-x64 self-contained ReadyToRun 패키지를 만든다.
- 패키지에는 실행 파일, .NET runtime, native SQLite와 Claude/Codex Hook wrapper가 포함된다.
- ZIP, SHA-256 파일과 파일별 해시가 있는 `contents.json`을 생성한다.
- runtime이 없는 clean-install, 공백·한글 경로, Hook wrapper smoke를 수행한다.
- Release build와 전체 단위·통합 테스트가 통과한다.

### 미구현 항목

- GitHub Release workflow와 버전 태그 규칙
- `task-token-meter --version`
- 태그 버전의 assembly/file/informational version 주입
- 사용자용 설치·업데이트·제거 스크립트
- npm launcher 및 플랫폼 패키지
- WinGet manifest
- Windows 코드 서명
- 공개 배포 라이선스

현재 `.github/workflows/ci.yml`은 restore, build, test만 수행하며 Release asset을 만들거나 게시하지 않는다. 저장소에는 아직 버전 태그와 GitHub Release가 없다.

## 참고한 AI CLI 배포 사례

### Codex CLI

Codex는 플랫폼별 네이티브 바이너리를 Release에 두고 독립 설치기, npm, Homebrew를 제공한다. npm은 얇은 메타 패키지가 OS·CPU별 네이티브 패키지를 optional dependency로 선택하는 구조다.

- [Codex 설치 문서](https://github.com/openai/codex/blob/main/README.md?plain=1)
- [Codex npm 패키징](https://github.com/openai/codex/blob/main/codex-cli/scripts/build_npm_package.py)

적용점: GitHub Release를 원본으로 두고 npm은 플랫폼 선택과 command shim만 담당한다.

### Claude Code

Claude Code는 npm 설치에서 네이티브 설치기와 자체 업데이트 중심으로 이동했다. 설치 방식 진단, 자동 업데이트와 수동 `claude update`를 제공한다.

- [Claude Code 설치 문서](https://docs.anthropic.com/en/docs/claude-code/getting-started)

적용점: 사용자가 설치 방식이나 runtime을 관리하지 않도록 설치·업데이트 경로를 제품 수준으로 제공한다.

### ccusage

ccusage는 `bunx`, `pnpm dlx`, `npx` 직접 실행과 전역 npm 설치를 제공한다. JavaScript 생태계 사용자에게 설치 없는 첫 실행과 간단한 업데이트 경험을 제공한다.

- [ccusage 설치 문서](https://github.com/ccusage/ccusage/blob/main/docs/guide/installation.md)

적용점: `npx task-token-meter@next`를 체험 경로로 제공하되, 네이티브 패키지의 원본은 GitHub Release로 유지한다.

### OpenCode

OpenCode는 설치 스크립트를 기본 경로로 두고 npm, Homebrew 등 여러 채널을 제공한다. npm 패키지는 postinstall로 네이티브 바이너리를 선택하므로 Bun과 pnpm에서 lifecycle script 허용 옵션이 필요하다.

- [OpenCode 설치 문서](https://opencode.ai/v2/docs)

적용점: 네트워크 다운로드 postinstall에 의존하지 않고 플랫폼 패키지 자체에 검증된 바이너리를 포함한다.

### Aider

Aider는 한 줄 설치기가 격리된 Python 환경과 필요한 runtime을 구성해 사용자가 의존성 충돌을 직접 해결하지 않게 한다.

- [Aider 설치 문서](https://aider.chat/docs/install.html)

적용점: Task Token Meter도 self-contained 패키지를 설치해 .NET SDK/runtime을 사용자 선행 조건으로 만들지 않는다.

## 배포 채널

| 채널 | 역할 | 사용자 경험 | 도입 시점 |
|---|---|---|---|
| GitHub Release | 바이너리·해시·manifest의 단일 원본 | 수동 다운로드 가능 | 1단계 |
| PowerShell installer | Windows 기본 설치·업데이트·제거 | 한 줄 설치, 관리자 권한 불필요 | 1단계 |
| npm | AI CLI 사용자를 위한 보조 설치·직접 실행 | `npx`, `npm install -g` | 2단계 |
| WinGet | Windows 기본 패키지 관리자 통합 | install/upgrade/uninstall | 안정판 이후 |

`.NET tool` 배포는 기본 채널로 사용하지 않는다. 실행에 적합한 .NET runtime 또는 SDK를 요구해 현재 self-contained 패키지의 장점을 없애기 때문이다.

## 버전과 배포 채널 규칙

- SemVer 태그를 사용한다: `vMAJOR.MINOR.PATCH`.
- prerelease는 `v0.1.0-preview.1` 형식을 사용한다.
- GitHub prerelease와 npm `next` dist-tag를 연결한다.
- GitHub stable release와 npm `latest` dist-tag를 연결한다.
- 동일 태그의 산출물을 교체하지 않는다. 수정이 필요하면 새 patch 또는 prerelease 번호를 발행한다.
- 실행 파일의 product/file/informational version과 `task-token-meter --version`은 태그 버전과 일치해야 한다.
- Release asset 이름은 최신 다운로드 URL을 유지할 수 있도록 버전과 무관하게 고정한다.

Release asset은 다음과 같다.

```text
task-token-meter-win-x64.zip
task-token-meter-win-x64.zip.sha256
contents.json
install.ps1
uninstall.ps1
```

Release 본문에는 지원 Provider 버전, OS/RID, 알려진 제한, acceptance 결과와 변경 사항을 포함한다.

## GitHub Release workflow

`.github/workflows/release.yml`을 추가한다.

### Trigger

- `v*` 태그 push에서 실행한다.
- workflow가 태그 형식과 대상 commit이 `main` 계보인지 확인한다.
- 별도의 `workflow_dispatch`는 build 검증에만 사용하고 이미 게시된 태그의 산출물을 교체하지 않는다.

### Build와 검증

Windows GitHub-hosted runner에서 다음을 순서대로 수행한다.

1. 전체 history와 tag를 checkout한다.
2. .NET SDK를 `global.json` 기준으로 설정한다.
3. `dotnet restore --locked-mode`를 실행한다.
4. Release build와 전체 테스트를 실행한다.
5. 태그 버전을 MSBuild `Version`, `FileVersion`, `InformationalVersion`에 전달한다.
6. `Build-WindowsPackage.ps1`로 패키지를 만든다.
7. PowerShell 5.1 smoke와 패키지 위생 검사를 수행한다.
8. ZIP SHA-256과 `contents.json`을 재검증한다.
9. 산출물에 GitHub artifact provenance를 생성한다.
10. Draft GitHub Release를 만들고 asset을 첨부한다.

GitHub Release는 자동으로 공개하지 않는다. maintainer가 draft의 태그, 해시, 파일 수, 설치 smoke와 release note를 확인한 뒤 게시한다. preview 태그는 prerelease로 표시한다.

GitHub artifact attestation은 빌드 workflow, 저장소와 commit SHA를 산출물에 연결한다. 사용자는 다음과 같이 provenance를 확인할 수 있다.

```powershell
gh attestation verify .\task-token-meter-win-x64.zip `
  --repo jaywapp/task-token-meter
```

- [GitHub artifact attestation 문서](https://docs.github.com/en/actions/concepts/security/artifact-attestations)

## PowerShell 설치기

### 사용자 명령

안정판 기본 설치:

```powershell
irm https://github.com/jaywapp/task-token-meter/releases/latest/download/install.ps1 | iex
task-token-meter --version
```

원격 스크립트를 먼저 검토하는 설치:

```powershell
Invoke-WebRequest `
  https://github.com/jaywapp/task-token-meter/releases/latest/download/install.ps1 `
  -OutFile install-task-token-meter.ps1

Get-Content .\install-task-token-meter.ps1
.\install-task-token-meter.ps1
```

preview 또는 특정 버전 설치는 저장한 스크립트에 명시적 옵션을 전달한다.

```powershell
.\install-task-token-meter.ps1 -Version 0.1.0-preview.1
```

### 설치 동작

- Windows x64만 허용하고 지원하지 않는 OS/architecture는 변경 없이 종료한다.
- GitHub Release에서 ZIP과 SHA-256을 HTTPS로 내려받는다.
- 압축을 풀기 전에 SHA-256을 검증한다.
- 기본 설치 위치는 `%LOCALAPPDATA%\Programs\TaskTokenMeter\<version>`이다.
- `%LOCALAPPDATA%\Programs\TaskTokenMeter\bin`에 고정 command shim을 둔다.
- 사용자 PATH에 `bin`을 중복 없이 추가한다.
- 설치 상태를 `install-state.json`에 기록한다.
- 설치 후 `task-token-meter --version`과 `--help`를 실행한다.
- 실패하면 기존 활성 버전을 유지하고 임시 다운로드만 정리한다.
- 관리자 권한이나 시스템 PATH 변경을 요구하지 않는다.
- Claude/Codex Hook은 자동 설치하지 않는다. 사용자가 provider별 install 명령을 명시적으로 실행한다.

업데이트는 같은 설치 명령을 다시 실행한다. 새 버전을 별도 디렉터리에 설치하고 검증한 뒤 command shim이 가리키는 버전을 바꾼다. 실행 중인 이전 버전과 파일 lock이 충돌하지 않도록 in-place 덮어쓰기를 피한다.

제거 스크립트는 Task Token Meter가 소유한 설치 디렉터리와 자신이 추가한 PATH 항목만 제거한다. ledger와 Hook 설정은 기본적으로 보존하며, 명시적 옵션이 있을 때만 삭제한다.

## npm 배포

npm은 GitHub Release와 PowerShell 설치기가 안정된 뒤 추가한다.

### 사용자 명령

```powershell
npx task-token-meter@next --help
npm install -g task-token-meter@next
```

안정판 이후에는 `@latest`를 기본으로 한다.

```powershell
npx task-token-meter@latest --help
npm install -g task-token-meter@latest
```

### 패키지 구조

- `task-token-meter`: JavaScript launcher, `bin` 등록, 플랫폼 선택과 오류 메시지
- `@jaywapp/task-token-meter-win32-x64`: Release에서 검증한 self-contained 패키지 내용

메타 패키지는 플랫폼 패키지를 optional dependency로 참조한다. launcher는 설치된 플랫폼 패키지의 `task-token-meter.exe`를 인자·stdin·stdout·stderr를 그대로 유지하며 실행하고 exit code를 전달한다.

postinstall에서 임의 URL의 바이너리를 다운로드하지 않는다. lifecycle script 비활성화, proxy, offline cache와 package-manager별 신뢰 정책 때문에 설치 성공 여부가 달라질 수 있기 때문이다.

npm publish는 장기 `NPM_TOKEN` 대신 GitHub Actions OIDC trusted publishing을 사용한다. public package는 provenance를 활성화한다.

- [npm trusted publishing 문서](https://docs.npmjs.com/trusted-publishers/)

2026-09-21 조회 시 `task-token-meter` package는 npm registry에서 E404였지만 이름 사용 가능 여부는 최초 publish 직전에 다시 확인한다.

## WinGet 배포

안정판이 게시된 뒤 `Jaywapp.TaskTokenMeter` ID로 WinGet community repository 제출을 준비한다.

```powershell
winget install Jaywapp.TaskTokenMeter
winget upgrade Jaywapp.TaskTokenMeter
winget uninstall Jaywapp.TaskTokenMeter
```

manifest는 GitHub Release의 고정 asset URL과 SHA-256을 참조한다. 제출 전 Windows Sandbox에서 install, upgrade, uninstall과 PATH 정리를 검증한다.

- [WinGet manifest 문서](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest)
- [WinGet 제출 절차](https://learn.microsoft.com/en-us/windows/package-manager/package/repository)

## 보안과 공급망

- Release workflow의 action은 commit SHA로 pin한다.
- workflow permission은 job별 최소 권한을 사용한다.
- 일반 CI는 `contents: read`를 유지한다.
- Release job만 `contents: write`, attestation에 필요한 `id-token: write`와 `attestations: write`를 갖는다.
- npm은 OIDC trusted publishing을 사용하고 장기 publish token을 저장하지 않는다.
- ZIP, installer, manifest와 checksum에 secret-like content와 raw Provider log가 없는지 검사한다.
- `.pdb`, source map, `.jsonl`, `.log`, `.env`, source file을 package에서 거부한다.
- Windows 코드 서명 인증서를 도입하면 publish 전에 exe와 installer signature를 검증한다.
- 설치기는 HTTPS 다운로드 후 checksum이 일치하지 않으면 설치하지 않는다.

## Release gate

### Preview release 조건

- Critical/High 미해결 이슈가 없다.
- 전체 build/test와 package smoke가 통과한다.
- ZIP, SHA-256, manifest와 installer가 서로 일치한다.
- preview임과 `PERF-001`, `SCOPE-001`, `CONSISTENCY-001` 제한을 Release 본문에 표시한다.
- 실제 사용자 Hook을 자동 설치하지 않는다.

### Stable release 조건

- `PERF-001`의 공식 21 MiB/100,000행 warm p95가 1,000 ms 이하이고 peak RSS가 256 MiB 이하이다.
- 실제 지원 Provider 버전에서 sanitized transcript/rollout smoke를 통과한다.
- 실제 사용자 설정을 복제한 임시 환경에서 Hook install/status/uninstall을 검증한다.
- PowerShell installer의 clean install, upgrade, rollback, uninstall 테스트가 통과한다.
- 라이선스와 지원 정책이 저장소 및 package metadata에 명시된다.
- Windows 서명 정책과 미서명 바이너리 제한을 문서화하거나 코드 서명을 적용한다.

## 선행 결정

외부 게시 구현 전에 다음 결정을 확정해야 한다.

1. 공개 배포 라이선스: MIT 등 허용 범위
2. 최초 prerelease 버전: 권장 `v0.1.0-preview.1`
3. npm publisher와 scope: unscoped `task-token-meter`, 플랫폼 package용 `@jaywapp`
4. Release 승인 방식: GitHub Environment reviewer 또는 draft 수동 게시
5. Windows 코드 서명 도입 시점

## 구현 순서

### Phase 1 — GitHub prerelease와 설치기

- 라이선스 추가
- `--version`과 버전 metadata 구현
- package script의 버전 입력·manifest 기록
- `install.ps1`, `uninstall.ps1` 및 Pester 또는 PowerShell integration test 추가
- tag 기반 `release.yml`과 draft prerelease 생성
- README 설치·업데이트·제거 문서 갱신

### Phase 2 — npm `next`

- launcher와 win32-x64 package 구성
- `npm pack` 내용·크기·secret scan 검증
- Windows npm/npx 설치·실행·제거 E2E
- npm OIDC trusted publisher 설정
- preview를 `next` dist-tag로 게시

### Phase 3 — Stable과 WinGet

- release blocker 해소와 acceptance 재측정
- 코드 서명 정책 확정
- stable GitHub Release와 npm `latest` 게시
- WinGet manifest 생성·Sandbox 검증·community repository 제출

## 완료 기준

- 새 Windows 사용자에게 저장소 clone이나 .NET 설치 없이 한 개의 PowerShell 명령으로 설치된다.
- 설치 후 새 terminal에서 `task-token-meter --version`과 `--help`가 성공한다.
- 같은 설치 명령으로 안전하게 업데이트되고 실패 시 기존 버전이 유지된다.
- 제거 후 설치 파일과 소유한 PATH 항목만 없어지고 사용자 ledger/Hook 설정은 보존된다.
- GitHub Release, npm package와 WinGet manifest가 같은 tag, binary hash와 provenance를 가리킨다.
- stable 채널은 문서화된 release acceptance를 모두 통과한 버전만 제공한다.
