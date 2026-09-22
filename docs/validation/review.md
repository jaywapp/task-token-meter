# TASK-014 교차 리뷰

검토일은 2026-09-20이다. 구현 전체를 정확성, 개인정보, 비차단 동작, crash/retry 회복, 패키징 위생 관점에서 독립 검토했다. 제품 코드는 커밋·push·배포하지 않았고 실제 Provider 설정에 Hook을 설치하지 않았다.

이 문서는 2026-09-20 기준 리뷰 결과다. 아래 `PERF-001` 관련 판정은 2026-09-21 Codex adapter streaming projection 최적화로 해소되었으며, 갱신된 수치와 근거는 [performance.md](performance.md)와 [acceptance.md](acceptance.md)에 있다. 나머지 발견 항목은 이 문서의 기록을 유지한다.

리뷰 시점의 전체 release acceptance는 **Fail**이었다. Critical/High 미해결 항목은 없었지만, 이미 검증된 `PERF-001`이 release blocker로 열려 있었다. 공식 21 MiB/100,000행 warm p95는 1,648.19 ms로 1초 기준을 넘었다.

## 결과 요약

| 심각도 | 발견 | 해결 | 미해결 |
|---|---:|---:|---:|
| Critical | 0 | 0 | 0 |
| High | 3 | 3 | 0 |
| Medium | 3 | 2 | 1 |
| Low | 1 | 1 | 0 |

미해결 Medium은 `CONSISTENCY-001`이다. 현재 동작과 완료 조건을 아래에 적었으며 acceptance에서 숨기지 않았다.

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

## 2026-09-22 실제 Provider 로그 검증에서 발견한 사항 — High 2건, 해결

`v0.1.0-preview.3`를 이 머신에 실제로 설치하고 Claude Code 2.1.278·Codex CLI 0.153.4 양쪽에 Hook을 등록한 뒤, 처음으로 실제(합성이 아닌) transcript/rollout으로 조회를 검증했다. 이전 검증은 모두 합성 fixture였다는 제한이 여기서 실제로 드러났다. 두 건 모두 재현하고 고쳤다.

### H-004 — `--provider`가 다른 Provider 소스 읽기를 막지 못함 — 해결

- **근거:** `AdapterCliRuntime.ReadAll()`이 요청받은 provider와 무관하게 `adapters` 딕셔너리 전체를 순회했다. `Discover(provider, sessionId)`는 `ReadAll()`로 전체를 먼저 읽은 뒤에야 provider로 필터링했다(`AdapterCliRuntime.cs:46-48`, 수정 전 기준).
- **재현:** 이 머신에서 `current --provider claude --session <실제 session id>`가 `exit 5 unsupported_schema`로 실패했다. `TOKEN_METER_CODEX_SOURCES`를 빈 디렉터리로 돌리면 같은 명령이 성공해 실제 usage를 반환했다 — Codex 쪽 소스(H-005) 문제가 Claude 전용 조회까지 막고 있었다.
- **영향:** 두 Provider를 함께 쓰는 환경에서는 한쪽 Provider의 실제 로그에 스키마 문제가 하나만 있어도 다른 Provider를 포함한 모든 명령이 실패한다. 오류 메시지도 어느 Provider가 원인인지 구분하지 않는다.
- **수정:** `AdapterCliRuntime.cs`의 `ReadAll`이 `ProviderKind?` 필터를 받는다. `Discover`는 `--provider`가 명시되면 그 Provider의 adapter만 읽는다. `ReadSession`(→`ReadTurnsAsync`/`SyncAsync`/`RebuildAsync`)은 항상 구체적인 `session.Provider`를 알고 있으므로 그 Provider만 읽는다. `--provider` 없이 후보를 나열하는 경우(대화형 선택)는 기존과 같이 두 Provider를 모두 읽는다.
- **회귀:** `AdapterCliRuntimeTests.cs`의 `ExplicitProviderIsolatesReadingFromAnUnsupportedOtherProviderSource` — Codex 소스를 의도적으로 깨뜨려 두고 `--provider claude`가 영향받지 않음을 확인한다.

### H-005 — Codex 실제 rollout의 `token_usage_record`를 하나도 인식하지 못함 — 해결

- **근거:** `CodexLineScanner.Scan`이 `token_usage_record`를 `{"type":"event_msg","payload":{"type":"token_usage_record", ...}}` 형태로만 인식했다(root `type`이 `event_msg`이고 그 `payload.type`이 `token_usage_record`). 그런데 이 머신에 설치된 Codex CLI 0.153.4 실제 rollout은 `token_usage_record`를 **root `type`으로 직접** 쓰고, session/turn/usage 필드는 `event_msg` 래퍼 없이 바로 `payload` 아래에 있다. 모든 fixture(`tests/fixtures/codex/*.jsonl`)가 전자만 모델링하고 있었다.
- **재현:** 이 머신의 실제 `~/.codex/sessions` 776개 rollout 파일 전부에서 `CodexUsageAdapter.ReadDetailed`가 `tokenRecordCount=0`, `IsSupported=false`, 진단 `no_token_usage_records`를 반환했다. 실제 Codex 세션 하나(`current --provider codex --session <실제 id>`)로도 같은 실패를 재현했다.
- **영향:** 이 문제가 고쳐지기 전에는 설치된 Codex CLI 0.153.4에서 실제 usage가 **0%** 측정됐다. `docs/research/provider-contracts.md`가 `payload.session_id` 등으로 문서화한 실제 관측 형태 자체는 맞았지만, 구현의 root-type 분류 조건이 그 형태를 반영하지 못했다.
- **수정:** `CodexLineScanner.cs`의 `CodexRootType`에 `TokenUsageRecord`를 추가하고 `ReadRootType`이 root 리터럴 `"token_usage_record"`도 인식하게 했다. `Scan`은 root type이 `TokenUsageRecord`면 곧바로 `CodexLineKind.TokenUsageRecord`로 분류한다. 기존 `event_msg`+중첩 `payload.type` 경로는 그대로 남겨 뒀다(다른 소스가 그 형태를 쓸 가능성을 배제하지 않기 위해서다). 필드 파싱(`ScanPayload`)은 두 형태에서 동일하므로 변경하지 않았다.
- **검증:** 수정 후 같은 776개 실제 rollout에서 실제 세션이 정상적으로 usage를 반환했다(예: `processedTokens=183941`). 기존 Codex fixture 전부는 변경 없이 그대로 통과한다.
- **회귀:** `CodexAdapterTests.cs`의 `ReadDetailedRecognizesTheRealRootLevelTokenUsageRecordShape` — 새 fixture `tests/fixtures/codex/real-root-shape.jsonl`(root-level 형태, 실제 값은 노출하지 않는 합성 데이터)로 `two-turn-snapshots.jsonl`의 T1과 같은 수치를 검증한다.

두 건 모두 실제 개인 데이터의 정확한 값이나 경로는 이 문서·커밋·fixture에 남기지 않았다. 재현에 쓴 수치(파일 개수, 반환된 token 합계)만 기록했다.

### SCOPE-001 — 자동 session discovery가 workspace를 필터하지 않음 — 해결 (2026-09-22)

- **근거:** `AdapterCliRuntime.Discover`가 Provider source root 전체를 읽고 provider/session만 필터했다. `--workspace`는 ledger route에만 쓰였고 discovery 계약에는 들어가지 않아, 같은 기본 source root에 여러 workspace 기록이 있으면 interactive 후보에 다른 workspace session이 섞였다.
- **수정:**
  1. `src/TaskTokenMeter.Core/Identity/WorkspaceRoot.cs`(신규): 디렉터리를 가장 가까운 Git root로 정규화한다(`.git` 디렉터리와 submodule의 `.git` 파일 모두 인식). 기록된 경로가 더는 존재하지 않으면(로그 이후 삭제·이동) 정규화만 하고 walk는 하지 않는다.
  2. Claude: `ClaudeAdapter.cs`의 `ParsedRecord`가 root-level `cwd`를 읽고 `CallResult`까지 전달한다. `BuildTurns`가 한 Turn을 구성하는 call들의 canonical workspace가 정확히 하나로 일치하면 그 값을, 0개면 `null`(unknown)을, 2개 이상(드문 turn 중간 디렉터리 변경)이면 `null`과 `workspace_conflict` 진단을 남긴다.
  3. Codex: `CodexLineScanner.cs`가 root-level `turn_context`(주 출처, `cwd`)와 `session_meta`(폴백, `cwd`)를 인식하도록 `CodexRootType`/`CodexLineKind`에 `TurnContext`를 추가했다. `CodexUsageAdapter.BuildRootTurn`이 turn별로 turn_context 우선, 없으면 session_meta로 같은 canonical-일치 규칙을 적용한다.
  4. `AdapterCliRuntime.ReadAll`이 `(TurnProjection, WorkspaceRoot)` 튜플을 내보내고, `Discover(provider, sessionId, workspace)`가 요청 workspace를 canonical화해 **known이고 다른 workspace**인 후보만 제외한다. workspace를 판정할 수 없는 후보는 숨기지 않는다(불확실한 데이터를 조용히 감추는 쪽이 더 나쁘다는 이 저장소의 기존 원칙과 동일). `SessionSelectionRequest`에 `Workspace`를 추가해 CLI의 `--workspace`(생략 시 기존 기본 workspace 결정 로직 그대로)가 그대로 전달된다.
  5. 명시 `--session`이 known-mismatch 후보를 가리키면 필터 단계에서 이미 후보 목록에서 빠지므로, `SessionSelector`가 `CandidateUnavailable`(exit 3)을 반환한다 — 별도 우회 플래그 없이 같은 workspace를 명시하면 정상 동작한다.
- **실제 데이터 검증:** 이 머신의 실제 `~/.claude/projects`(344개 파일, 269개 세션)에서 특정 workspace로 필터하면 후보가 269개→19개로 줄었고, 다른 workspace의 세션은 정확히 제외됐다. 단일 세션 파일 안에서 turn마다 다른 두 개의 실제 canonical workspace를 정확히 구분했고, 실제로 두 디렉터리를 오간 turn 하나를 `workspace_conflict`로 정확히 표시했다(추측하지 않음). 경로 자체는 이 문서에 남기지 않았다.
- **회귀:** `tests/TaskTokenMeter.UnitTests/WorkspaceRootTests.cs`(정규화 자체: git root, submodule `.git` 파일, 하위 디렉터리에서 walk-up, 존재하지 않는 경로, 대소문자 무시), `tests/TaskTokenMeter.IntegrationTests/WorkspaceDiscoveryTests.cs`(완료 조건 4번: known-다른-workspace 제외와 unknown 유지, interactive 0/1/N, CI/JSON selector-required, 명시 session의 known mismatch가 자동 수락되지 않음), `CodexAdapterTests.ReadDetailedResolvesWorkspaceFromTurnContextWithSessionMetaFallback`(turn_context 우선, session_meta 폴백).

## 열린 Medium

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
| performance acceptance | 리뷰 시점 **Fail — PERF-001**, 2026-09-21 해소 |

리뷰 당시 성능 수치는 TASK-013의 검증된 결과를 유지했다. TASK-014에서 성능 최적화를 억지로 추가하지 않았으며 [performance.md](performance.md)의 재현과 완료 조건을 변경하지 않았다. `PERF-001`은 이후 별도 작업에서 Codex adapter streaming projection으로 해소했고, 같은 fixture 재측정에서 warm p95 821.47 ms를 기록했다.
