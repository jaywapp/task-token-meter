# 최종 수용 검증 — TASK-013 + TASK-014

## 판정

전체 판정은 **Pass**다(2026-09-22, `SCOPE-001` 해소 이후). TASK-014 High 수정 후 Release 전체 테스트는 134/134(단위 54, 통합 80) 통과했고, `PERF-001` 최적화 뒤 재실행에서는 147/147(단위 67, 통합 80) 통과했다. 2026-09-22 실제 Provider 로그 검증(H-004/H-005)과 `SCOPE-001` 구현 이후에는 176/176(단위 85, 통합 91) 통과한다. 두 Provider의 합성 전체 흐름과 이 머신의 실제 로그, 저장·migration, Hook, 패키징, RSS와 Hook latency는 검증되었다.

`PERF-001`은 **해소**되었다. Codex adapter를 streaming projection으로 바꿔 공식 21 MiB/100,000행 조회의 warm p95가 1,648.19 ms에서 821.47 ms로 내려가 1초 목표를 만족한다([performance.md](performance.md)). `SCOPE-001`도 **해소**되었다. 자동 session discovery가 이제 Provider 로그에서 추출한 canonical workspace로 후보를 필터한다([review.md](review.md)). 남은 항목은 release를 막지 않는 Medium `CONSISTENCY-001` 하나다.

## 기능 요구사항

| 요구사항 | 판정 | 증거/제한 |
|---|---|---|
| FR-01 current | Pass | `EndToEndTests.SyntheticProviderFlow...`의 Claude/Codex current JSON·text 비교 |
| FR-02 last | Pass | terminal 우선/provisional 결과와 failed terminal 회귀 `CliTests.LastPrefersTerminalAndCurrentIncludesRunning` |
| FR-03 turns | Pass | Provider별 E2E 시간순 Turn과 usage 전 필드 비교 |
| FR-04 선택자/workspace | Pass | 명시 selector·한글 workspace·PS 5.1, 자동 discovery의 canonical workspace 필터(`SCOPE-001` 해소) 모두 Pass |
| FR-05 세션 선택 | Pass | 0/1/N, 재검증, 명시 provider/session, workspace 불일치 후보 제외와 unknown 유지 Pass |
| FR-06 실제 usage/결손 | Pass (합성) | normalization과 Claude/Codex partial/invalid/total-only fixture |
| FR-07 dedupe | Pass | streaming alias/turn snapshot/replay/fork fixture; TASK-014 same-session fork replay 회귀 |
| FR-08 JSON/text 동일성 | Pass | E2E가 scope·quality·표시 usage를 비교하고 JSON stdout 단일 객체 검증 |
| FR-09 sync/rebuild/보관 | Pass | sync→source 삭제→stored fallback, adapter runtime tests |
| FR-10 Hook | Pass (합성) | 설치/제거·설정 보존·neutral stdout·비차단 실패·junction escape 회귀 |
| FR-11 상태/품질 | Pass | observed→stored provisional, strict, failed last와 incomplete diagnostics |
| FR-12 계측 범위 | Pass (합성) | Claude/Codex child·root scope, membership/attribution fixture |
| FR-13 저장 모드 | Pass | global↔workspace, crash retry, late writer/source manifest conflict, ledger concurrency |
| FR-14 대화형 CLI | Pass (동작 계약) | 0/1/N, 재입력, 취소, 비대화형 즉시 종료. 후보 workspace scope는 FR-04/05 제한 적용 |

## 설계 Success Criteria

| 기준 | 판정 | 증거/제한 |
|---|---|---|
| FR-01~14와 두 Provider | Pass | 합성 Provider 흐름과 실제 로그, FR-04/05의 canonical workspace 필터(`SCOPE-001` 해소) Pass |
| 중복·부분 streaming·child 지연·중단·재개·모델 변경 | Pass | unit fixture suite와 expected oracle |
| 동일 scope 참조 집계 | Pass (합성 oracle) | expected JSON의 수기 산식과 runtime equality |
| Hook 실패 시 Provider 지속·sync 복구 | Pass | timeout/start failure 비차단, source 복원 worker 저장 |
| 0/unknown/provisional 구별 | Pass | normalization과 CLI JSON/text |
| UC-001~008 범위 일치 | Pass | 비용 추정 Deferred, 번호 기반 CLI·두 storage mode 구현 |
| migration 재시도/중복 방지·비대화형 무대기 | Pass | crash boundary, retry replan, 8 writer, selector read count 0 |
| 20 MiB/100,000행 p95 ≤1초 | Pass | p95 821.47 ms, peak RSS max 89.02 MiB. [performance.md](performance.md) |

## TASK-014 리뷰 결과

| 심각도 | 발견 | 해결 | 미해결 |
|---|---:|---:|---:|
| Critical | 0 | 0 | 0 |
| High | 3 | 3 | 0 |
| Medium | 3 | 2 | 1 |
| Low | 1 | 1 | 0 |

해결한 High는 Codex same-session fork replay identity, migration 중 writer/source 변경, Hook junction allowlist escape다. 이후 실제 로그 검증에서 발견한 workspace-blind discovery `SCOPE-001`도 해소했다. 열린 Medium은 parse/fingerprint TOCTOU `CONSISTENCY-001` 하나이며, release acceptance를 막지 않는 제한으로 남긴다. 근거, 재현, 파일/줄, 완료 조건은 [review.md](review.md)에 있다.

## Provider 및 실행 환경 지원 행렬

| 대상 | 설치/기준 버전 | 구현 지원 | 이번 검증 | 제한 |
|---|---|---|---|---|
| Claude Code | 2.1.278 | transcript assistant usage / Stop, SubagentStop, StopFailure | 합성 E2E Pass; 2026-09-22 실제 개인 transcript·Hook 설치 Pass(H-004 수정 후) | — |
| Codex CLI | 0.153.4 | token_usage_record delta/turn/session / Stop, SubagentStop, Interrupt | 합성 E2E Pass; 2026-09-22 실제 rollout 776개·Hook 설치 Pass(H-005 수정 후) | — |
| .NET | SDK 10.0.400, runtime 10.0.11 | net10.0 / win-x64 self-contained | locked restore/build/publish Pass | 다른 RID Not Run |
| Windows PowerShell | 5.1.26100.9444 | redirect/pipe wrapper | actual process smoke Pass | actual interactive TTY Not Run |
| PowerShell | 7.6.5 | build/validation scripts | Pass | 없음 |

지원 버전과 다른 Provider에는 Hook을 설치하지 않으며 schema는 exit code 5로 거부한다. 2026-09-22 이 머신에서 실제 Claude Code·Codex 설정에 Hook을 설치하고 실제 transcript/rollout으로 조회해 두 건의 High(`H-004`, `H-005`, [review.md](review.md))를 발견·수정했다. 실제 개인 로그 검증은 이 한 대의 머신 기준이며, 다른 workspace 구성·오래된 세션 형식까지 전부 확인한 것은 아니다.

## 선택 환경 행렬

| 환경 | 선택자 없음 | 기대 code/read | stdout 계약 | 판정/증거 |
|---|---|---|---|---|
| interactive TTY | 0/1/N 후보 규칙, workspace 필터 | 3/0 또는 선택, 취소 130 | text | simulated console Pass; actual TTY Not Run; 후보 workspace 필터는 SCOPE-001 해소로 Pass |
| CI | 즉시 거부 | 4 / read 0 | stderr text | Pass |
| `--json` | 즉시 거부 | 4 / read 0 | selector JSON 한 개 | Pass, PS 5.1 actual process |
| stdin pipe | 즉시 거부 | 4 / read 0 | 오염 없음 | Pass |
| stdout pipe | 즉시 거부 | 4 / read 0 | 오염 없음 | Pass |
| `--non-interactive` | 즉시 거부 | 4 / read 0 | text error | Pass |
| Hook | payload provider/session | 0 / read 0 | Claude empty, Codex `{}`/empty | Pass |

`packaging/Smoke-PowerShell51.ps1`은 실제 Windows PowerShell 5.1 process에서 stdin pipe, stdout pipe, file redirection, JSON schema/수치, code 4를 합성 fixture로 검증했다.

## 내구성 및 저장

- `LedgerTests.EightConcurrentWritersCommitExactlyOneRowPerTurn`: global/workspace 각각 8 writer.
- `StorageMigrationTests.GlobalWorkspaceGlobalRoundTripPreservesProjectionAndSupersededUpdates`: 양방향 전환 뒤 usage/quality/membership 보존.
- `StorageMigrationTests.RetryAfterEveryCommitBoundaryConverges`: journal의 일곱 crash 경계 재시도.
- `StorageMigrationTests.WriterDuringMigrationWaitsAndIsRejectedAfterRouteSwitch`: migration과 runtime writer의 workspace lock 직렬화.
- `StorageMigrationTests.SourceChangeBeforeRouteSwitchKeepsOldRouteAndRetryReplansJournal`: live manifest conflict, 이전 route 유지, 안전한 retry replan.
- 늦은 writer, independent destination 충돌, readonly/disk/Git exclude 실패는 활성 source 유지와 자동 fallback 금지를 확인한다.

## 패키징

`packaging/Build-WindowsPackage.ps1`이 win-x64, self-contained, ReadyToRun으로 로컬 publish했다. 외부 publish는 하지 않았다.

| 검사 | 결과 |
|---|---|
| ZIP | `packaging/artifacts/task014-review-final-win-x64/task-token-meter-win-x64.zip`, 38,868,179 bytes |
| SHA-256 | `CEA9A31FB20D6CA6B9B68EC2F947E364A33476138234757019CA83A218B16CB8` |
| contents | 203 files, `contents.json`에 파일별 size/hash |
| native SQLite | `e_sqlite3.dll` 포함 Pass |
| runtime 없음 | 존재하지 않는 `DOTNET_ROOT`, multilevel lookup off 상태 clean-install `--help` Pass |
| path-independent wrapper | 다른 cwd와 공백·한글 install path에서 Claude empty/Codex `{}` Pass |
| package 위생 | `.pdb`, source map, `.jsonl`, `.log`, `.env`, source file, secret-like prefix 없음 |

## 성능과 남은 제한

`PERF-001`은 해소되었다. Codex adapter가 rollout을 UTF-8 byte 단위로 한 번만 훑는 streaming projection으로 바뀌었고, 지원하지 않는 record는 payload를 materialize하지 않으며, 관측값을 두 번 만들던 구조와 hot loop의 LINQ 할당이 사라졌다. incremental checkpoint는 도입하지 않았으므로 truncate/tail/parser-version fallback 경로도 새로 생기지 않았다. 같은 공식 fixture로 cold 1회/warm 30회를 다시 측정해 warm p95 821.47 ms(목표 ≤1,000 ms), peak RSS max 89.02 MiB(목표 ≤256 MiB)를 모두 만족했다. 집계·dedupe·lineage 결과와 CLI 출력, 종료 코드는 바뀌지 않았고 `tests/fixtures/expected/`의 수동 oracle도 그대로다. 측정과 변경 내역은 [performance.md](performance.md)에 있다.

`SCOPE-001`(FR-04/FR-05의 workspace 필터 부재)은 2026-09-22 해소했다. 남은 항목은 release를 막지 않는 `CONSISTENCY-001` 하나다. 완료 조건은 [review.md](review.md)에 있다.

## 재현 명령

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
