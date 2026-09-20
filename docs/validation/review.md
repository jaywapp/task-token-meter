# TASK-014 교차 리뷰

검토일은 2026-09-20이다. 구현 전체를 정확성, 개인정보, 비차단 동작, crash/retry 회복, 패키징 위생 관점에서 독립 검토했다. 제품 코드는 커밋·push·배포하지 않았고 실제 Provider 설정에 Hook을 설치하지 않았다.

전체 release acceptance는 **Fail**이다. Critical/High 미해결 항목은 없지만, 이미 검증된 `PERF-001`이 release blocker로 열려 있다. 공식 21 MiB/100,000행 warm p95는 1,648.19 ms로 1초 기준을 넘는다.

## 결과 요약

| 심각도 | 발견 | 해결 | 미해결 |
|---|---:|---:|---:|
| Critical | 0 | 0 | 0 |
| High | 3 | 3 | 0 |
| Medium | 3 | 1 | 2 |
| Low | 1 | 1 | 0 |

미해결 Medium은 `SCOPE-001`과 `CONSISTENCY-001`이다. 둘 다 현재 동작과 완료 조건을 아래에 적었으며 acceptance에서 숨기지 않았다.

## 수정한 발견 사항

### H-001 — Codex fork replay가 canonical 실행까지 제외할 수 있음 — 해결

- **근거:** Codex App Server 계약상 fork는 새 thread를 만들면서 기존 session lineage를 보존할 수 있다. 기존 제외 결과는 origin execution ID와 root session만 가져서 같은 root session 안의 canonical/replay를 구분하지 못했다.
- **재현:** `tests/fixtures/codex/fork-replay/fork-with-origin.jsonl`의 fork session ID를 parent와 같게 두면, origin이 같은 두 실행 중 replay만 제외되어야 한다.
- **수정:** `src/TaskTokenMeter.Adapters/Codex/CodexContracts.cs:156`의 제외 증거에 thread/turn을 추가하고, `CodexUsageAdapter.cs:541`에서 origin별 distinct thread/turn 중 canonical을 고른 뒤 replay의 정확한 identity만 제외한다.
- **회귀:** `CodexAdapterTests.cs:174`의 `ReadDetailedUsesOriginIdentityForForkReplayAndIsolatesAmbiguousReplay`.

### H-002 — migration 중 늦은 writer가 stale destination route를 활성화할 수 있음 — 해결

- **근거:** backup/import 뒤 route switch 전 source ledger writer가 commit하면 destination manifest에는 그 변경이 없는데도 이전 구현은 route만 바꿀 수 있었다. 이는 acknowledged projection을 잃는다.
- **재현:** migration을 route switch 직전에 멈추고 같은 workspace의 기존 route writer를 실행한다. writer를 조정하지 않으면 source가 destination import 뒤 바뀐다.
- **수정:** `LedgerContracts.cs:205`, `StorageRouting.cs:173`, `SqliteLedgerStore.cs:114`에서 일반 commit/purge와 migration이 같은 workspace lock을 사용한다. `StorageMigrationService.cs:179`는 switch 직전 live source route와 manifest를 다시 읽어 backup manifest와 다르면 이전 route를 유지하고 conflict를 반환한다. 기존 unfinished journal은 endpoint/generation/source/destination hash가 일치할 때만 이어가며, source가 바뀐 retry는 현재 상태로 다시 계획한다.
- **회귀:** `StorageMigrationTests.cs:169`의 `WriterDuringMigrationWaitsAndIsRejectedAfterRouteSwitch`, `:194`의 `SourceChangeBeforeRouteSwitchKeepsOldRouteAndRetryReplansJournal`.
- **deadlock 검토:** migration은 workspace lock을 가진 채 authority가 없는 destination store로 import하므로 같은 coordinator lock에 재진입하지 않는다. route switch는 registry lock만 취한다.

### H-003 — Hook transcript allowlist의 junction escape — 해결

- **근거:** lexical `Path.GetRelativePath`만 쓰면 allowed root 안의 directory junction이 root 밖을 가리켜도 `escape\source.jsonl`처럼 내부 상대 경로로 보인다.
- **재현:** Windows 임시 allowed root 안에 `mklink /J escape <sibling-outside>`를 만들고 outside JSONL을 `allowed\escape\source.jsonl`로 전달했다. 기존 검사는 `File.Exists=true`와 내부 상대 경로 때문에 수락했다.
- **수정:** `HookEntryPoint.cs:81`, `:136`, `:182`에서 allowlist root와 transcript의 각 기존 component를 `ResolveLinkTarget(true)`로 해석한다. 물리 target을 allowlist에 대조하고, 통과한 물리 경로만 worker에 전달한다.
- **회귀:** `HookTests.cs:180`의 `TranscriptPathThroughJunctionCannotEscapeConfiguredSourceRoot`가 실제 Windows junction을 만든다.

### M-001 — `last`가 failed terminal Turn을 건너뜀 — 해결

- **근거/재현:** completed/interrupted만 terminal로 선택해 더 최근 failed Turn이 있어도 오래된 terminal을 반환했다.
- **수정:** `CliApplication.cs:168`에 `ExecutionState.Failed`를 포함했다.
- **회귀:** `CliTests.cs:65`의 `LastPrefersTerminalAndCurrentIncludesRunning`.

### L-001 — stored fallback warning 중복 — 해결

- **근거/재현:** text renderer가 stored fallback을 별도 줄과 diagnostics 전체 줄에 모두 표시했다.
- **수정:** `CliApplication.cs:211`에서 diagnostics warning 한 줄만 렌더링한다.
- **회귀:** `CliTests.cs:94` 이후 assertion이 `stored_fallback` 한 번만 출력됨을 확인한다.

## 열린 Medium

### SCOPE-001 — 자동 session discovery가 workspace를 필터하지 않음

`AdapterCliRuntime.Discover`(`src/TaskTokenMeter.Cli/AdapterCliRuntime.cs:46`)는 Provider source root 전체를 읽고 provider/session만 필터한다. `--workspace`는 이후 ledger route에 사용되며 discovery 계약에는 들어가지 않는다. 따라서 여러 workspace 기록이 같은 기본 source root에 있으면 interactive 후보에 다른 workspace session이 섞일 수 있다. 명시 `--provider` + `--session` 조회는 동작하지만, FR-04와 FR-05의 workspace/session 안전성은 Partial이다.

완료 조건:

1. 지원 Provider metadata에서 canonical workspace identity를 추출하고 불명확한 기록은 명시적으로 unknown 처리한다.
2. discovery가 canonical `--workspace`를 입력받아 후보를 필터한다.
3. 명시 session의 workspace mismatch를 자동 수락하지 않는다.
4. 두 workspace가 섞인 fixture에서 interactive 0/1/N, CI/JSON selector, explicit mismatch 회귀 테스트를 통과한다.

### CONSISTENCY-001 — parse와 source fingerprint 사이 append TOCTOU

Adapter가 JSONL을 parse한 뒤 `AdapterCliRuntime.BuildSourceSnapshotsAsync`(`:176`)가 파일을 다시 읽어 `SourceFingerprint`(`:225`)와 extent를 만든다. 두 read 사이 append/truncate가 발생하면 한 번의 sync에 오래된 projection과 더 새로운 manifest가 함께 commit될 수 있다. 다음 sync에서 회복되지만 해당 commit의 source lineage가 동일 시점 snapshot을 보장하지 않는다.

완료 조건:

1. 동일한 immutable bytes/extent로 parse와 fingerprint를 계산하거나, 전후 metadata/hash 변화 시 전체 작업을 재시도한다.
2. append, truncate, partial tail, parser capability/version 변경 회귀 테스트에서 projection과 manifest가 같은 snapshot을 가리킨다.
3. 기존 dedupe, revision conflict, stored fallback 결과가 유지된다.

## 범위별 검토

| 영역 | 검토 결과 |
|---|---|
| Provider scope claims | Claude 2.1.278/Codex 0.153.4에만 구현 지원을 한정했다. documented/observed/unknown/unsupported를 research 문서와 fixture에 대조했다. 실제 개인 로그와 Hook 설치는 Not Run이다. |
| Batch dedupe/null/lineage/root scope | alias·snapshot·delta/session exclusion, null/total-only, child/root membership, replay/fork fixture를 대조했다. H-001 수정 후 ambiguous replay는 합산하지 않는다. |
| CLI | current/last/turns/sync/rebuild, strict, JSON 단일 객체, noninteractive selector, exit code를 대조했다. M-001/L-001을 수정했고 SCOPE-001을 열었다. |
| Source manifest/privacy | source path는 opaque hash ID로 바꾸고 bytes fingerprint, extent, generation, availability를 저장한다. body와 absolute source path는 ledger에 저장하지 않는다. CONSISTENCY-001은 열려 있다. |
| Ledger | SQLite transaction, expected revision, source generation/fingerprint, route generation/store ID, 8-writer 경쟁을 대조했다. commit/purge는 migration과 workspace lock을 공유한다. |
| Migration | global↔workspace roundtrip, 7 crash boundary, independent destination 충돌, Git exclude, readonly/write failure, late writer를 대조했다. switch 직전 live manifest 재검증과 retry replan을 추가했다. |
| Hook | 설정 보존/backup/idempotency, neutral stdout, process argument separation, fail-open, timeout, payload/path 검증을 대조했다. H-003 물리 경로 검증을 추가했다. |
| Packaging | win-x64 self-contained, native SQLite, clean runtime, 공백·한글 경로 wrapper, contents hash, 금지 확장자/secret-like prefix를 확인했다. |

## 민감 정보 검토

제품 입력 JSONL에는 prompt/message/tool body와 credential이 있을 수 있으므로 parser projection, DB schema, diagnostics, CLI renderer, docs, package를 따라 저장 경로를 추적했다.

- Ledger projection은 ID, usage, 상태, membership, quality, 진단 code만 저장한다.
- Source manifest는 opaque source ID, fingerprint, extent, generation만 저장한다.
- Hook diagnostics는 code/provider/event/duration만 저장하며 payload, session, transcript path를 기록하지 않는다.
- CLI 기본 출력은 session/Turn ID와 usage를 표시하지만 raw body/tool argument/env secret은 표시하지 않는다.
- Route registry/journal/status는 복구 계약상 workspace/data/database/backup absolute path를 로컬에 보관하거나 표시한다. 이는 README에 명시했다.
- source/docs/package scan에서 실제 secret, raw Provider body, 실제 사용자 absolute path를 찾지 못했다.
- `tests/fixtures`와 테스트 코드는 합성 ID, 임시 디렉터리, `raw prompt must be ignored`, `secret-tool-argument` 같은 비유출 assertion marker를 의도적으로 포함한다. 실제 사용자 데이터가 아니다.
- build/publish artifact의 `.pdb`, source map, `.jsonl`, `.log`, `.env`, source file, secret-like prefix는 없었다.

## 최종 검증

| 검사 | 결과 |
|---|---|
| solution locked restore | Pass |
| benchmark locked restore | Pass |
| Release build | Pass, warning 0/error 0 |
| 전체 테스트 | Pass, 134/134 — unit 54, integration 80 |
| format verify | Pass |
| vulnerable package audit | Pass, 알려진 취약 package 없음 |
| package | 203 files, 38,868,179 bytes |
| package SHA-256 | `CEA9A31FB20D6CA6B9B68EC2F947E364A33476138234757019CA83A218B16CB8` |
| self-contained/native SQLite/wrapper smoke | Pass |
| Windows PowerShell 5.1 | Pass, stdin/stdout/file redirect, JSON-only, selector code 4 |
| Markdown links/trailing whitespace/plan dependency scan | Pass |
| actual personal Provider logs | Not Run |
| actual Provider settings Hook install | Not Run |
| performance acceptance | **Fail — PERF-001** |

성능 수치는 TASK-013의 검증된 결과를 유지했다. TASK-014에서 성능 최적화를 억지로 추가하지 않았으며 [performance.md](performance.md)의 재현과 완료 조건을 변경하지 않았다.
