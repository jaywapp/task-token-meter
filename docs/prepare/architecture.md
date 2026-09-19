# Architecture — 기술 설계

작성일: 2026-09-19 · 상태: 조건부 추천안

## Architecture Overview

이 문서는 UC-001의 로그 중심 수집, UC-002의 C#/.NET CLI, UC-003의 전역 로컬 저장, UC-004의 SQLite, UC-005의 child 포함을 선택했을 때의 설계다. 선택을 확정한 문서가 아니며 다른 옵션을 고르면 관련 절과 `plan.md`를 먼저 변경한다.

```mermaid
flowchart LR
    L[Claude transcript / Codex rollout] --> D[Source discovery + stable snapshot]
    H[Provider command Hook] --> Q[동기화 요청]
    C[CLI 조회] --> D
    Q --> D
    D --> A[Provider Adapter]
    A --> I[Identity / revision / attribution]
    I --> N[Normalize + validate]
    N --> R[Turn projection]
    R --> O[Text / JSON renderer]
    R --> S[(SQLite Ledger)]
    S --> O
    N --> E[진단 코드·출처·측정 품질]
```

로그 읽기와 집계는 동일 서비스로 공유한다. 조회는 fresh snapshot 또는 source 부재 시 보관 snapshot을 출력하고, `sync`/Hook은 검증된 결과를 저장한다. 수집된 기록은 append 이벤트 자체가 아닌 logical record의 revision으로 해석한다.

## Technology Stack

| 후보/추천 | 이유 | 조건 |
|---|---|---|
| C# + 지원 중인 .NET LTS | Windows CLI 배포, 타입 있는 데이터 모델, SQLite 연동 | UC-002, 구현 시 지원 SDK 버전과 패키지 버전 고정 |
| System.Text.Json | 스트리밍 JSONL 읽기, 제품 runtime 내장 | 한 행 크기·정수 overflow 제한 필요 |
| Microsoft.Data.Sqlite + SQLite | transaction·unique key·upsert로 동시 writer 제어 | UC-004, bundled SQLite 버전과 보안 수정 확인 |
| xUnit 등 단일 테스트 러너 | golden fixture와 프로세스 통합 검증 | 구현 시작 시 유지 중인 버전 선택 |
| Python 표준 라이브러리 | 짧은 조사용 스크립트 후보 | 제품 runtime 추가 의존으로 만들지 않음 |

단일 파일 배포는 OS/architecture별 산출물이 필요하므로 하나의 exe로 모든 OS를 지원한다고 약속하지 않는다. Self-contained 여부와 native SQLite 파일 배치/추출을 패키징 테스트로 검증한다. [Microsoft 단일 파일 배포 문서](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

## System Components / Responsibilities

| 구성 요소 | 책임 | 금지 사항 |
|---|---|---|
| CLI | 명령 검증, 선택자, 출력·종료 코드 | 내부 집계 규칙 중복 구현 |
| Discovery | 허용된 source root에서 session/workspace 탐색 | 사용자 홈 전체 파일 내용 스캔 |
| ClaudeAdapter / CodexAdapter | 지원 형식 판별, native usage와 identity 추출 | 누락 필드 자동 0 채우기 |
| IdentityResolver | 재개/fork 중복·revision·실행 주체 구분 | 파일 경로만으로 동일 호출 판단 |
| AttributionResolver | root/child 관계와 귀속 증거 관리 | timestamp만으로 parent 추측 |
| UsageNormalizer | 필드 포함 관계와 불변식 검증 | 비용 계산과 합계 정책 혼합 |
| TurnProjector | root별 고유 실행 집합 집계, 품질 산출 | root inclusive 합계 + child를 재합산 |
| LedgerRepository | atomic commit, version, 재구축 이력 | 읽기 실패를 authoritative empty로 취급 |
| HookBridge | Provider payload 검증 및 재집계 요청 | model prompt/agent Hook, context 주입 |
| Diagnostics | 코드·개수·line index·시간 저장 | 대화/툴 인자/원본 payload 로그 |

## Data Flow

1. Provider·session·workspace를 확정한다. 모호한 세션은 CLI 오류로 반환한다.
2. 해당 session과 명시적으로 연결된 child source 목록, 파일 identity·크기·mtime을 캡처한다.
3. 스냅샷 크기까지만 읽는다. 마지막 미완성 행은 다음 scan으로 미루고 source 변경 여부를 다시 확인한다.
4. record UUID 중복을 제거하고 호출 identity별 revision을 고른다.
5. root 귀속을 해석하고 native usage를 정규화한다. 모순은 격리하고 정상 레코드는 유지한다.
6. root Turn마다 main과 고유 child 실행을 한 번씩 집계한다.
7. source completeness와 독립된 reference 유무로 measurement status를 계산한다.
8. 조회는 결과를 반환한다. 동기화는 짧은 transaction에서 검증된 snapshot을 저장한다.

조회 순서와 저장 순서는 독립이다. 늦게 시작한 worker가 먼저 끝난 뒤 오래된 worker가 덮어쓰는 것을 막기 위해 source generation과 기존 revision을 비교한다. 서로 비교할 수 없는 snapshot은 재읽고 재집계한다.

## Directory Structure

아래는 구현 단계의 예상 구조다. 이번 준비 작업에서는 생성하지 않는다.

```text
src/
  TaskTokenMeter.Cli/
  TaskTokenMeter.Core/          # identity, usage, attribution, projection
  TaskTokenMeter.Adapters/      # Claude, Codex, discovery
  TaskTokenMeter.Storage/       # schema, transactions, migrations
tests/
  TaskTokenMeter.UnitTests/
  TaskTokenMeter.IntegrationTests/
  fixtures/                    # synthetic or explicitly sanitized
docs/
  ideas/
  prepare/
  research/provider-contracts.md
  validation/acceptance.md
packaging/
  hooks/                       # templates, no live user settings
```

## Data Model

모든 토큰 수는 non-negative Int64 또는 `null`이다. 덧셈은 overflow를 검사한다. 시각은 UTC ISO 8601이며 원본 시각이 없으면 수집 시각과 혼동하지 않는다. JSON은 `schemaVersion`을 포함한다.

### 식별·저장 엔터티

| 엔터티 | 키 / 주요 필드 |
|---|---|
| Workspace | 로컬 `workspaceId`, canonical worktree path, 선택적 `repositoryGroupId` |
| Session | `(provider, sessionId)`, workspaceId, parentSessionId, providerVersion |
| Source | sourceId, sessionId, fileIdentity, content fingerprint, read extent, parserVersion, availability |
| Observation | observationId, sourceId, recordUuid, originExecutionId, usageKind, nativeUsage, fieldSemanticsVersion |
| Execution | `(provider, originSessionId, executionId)`, sourceTurnId, request/message aliases, model, native/normalized usage, revision |
| Turn | `(provider, rootSessionId, rootTurnId)`, lifecycle, measurementQuality, observedAt, revision |
| Membership | root Turn key + Execution key, relation evidence, attributionStatus |
| ContextSnapshot | Turn key + capturedAt + origin, clientVersion/model/effort, optional hashes/label |
| Diagnostic | scope key, code, count, sourceId/line index, occurredAt |

`root_turn_id`만으로 root session을 알아냈다고 간주하지 않는다. session lineage·parent metadata로 rootSessionId를 찾고, 찾지 못하면 `unattributed` observation으로 유지한다. child 실행을 child 상세와 root 상세 모두에서 보여줄 수 있지만 workspace 합계는 Membership의 실행 identity 합집합으로 계산한다.

원격 저장소 URL만으로 workspace를 합치지 않는다. 동일 remote의 서로 다른 worktree는 다른 workspaceId를 갖는다. remote를 저장할 때 credential/userinfo와 query를 제거하고, 자동 외부 전송은 하지 않는다. Windows 경로는 canonical absolute path와 filesystem case 규칙을 사용한다. symlink/junction은 허용 root 밖으로 나가면 거부한다.

### Native usage 보존

`nativeUsage`에는 Provider가 제공한 usage 객체와 그 숫자 필드 구조만 보존한다. transcript 전체, message content, Hook payload 전체를 저장하지 않는다. 알 수 없는 usage 필드는 원래 키·숫자/null 구조를 보존하고, 자유 문자열이나 큰 blob은 격리·진단한다. 추출 정책 버전과 redaction 여부를 저장한다. 이것이 본 설계에서의 native 보존 범위다.

### 정규화 계약

| 필드 | Claude | Codex |
|---|---|---|
| uncachedInput | input_tokens | input_tokens - cached_input_tokens, 두 값 모두 있을 때 |
| cacheRead | cache_read_input_tokens | cached_input_tokens |
| cacheWrite | cache_creation_input_tokens | cache_write_input_tokens를 보존; 포함 관계 미확인이면 합계 제외 |
| cacheWrite5m / cacheWrite1h | cache_creation TTL breakdown | null, TTL을 추정하지 않음 |
| inputTotal | fresh + read + write, 세 필드가 모두 알려졌을 때 | input_tokens |
| output | output_tokens | output_tokens |
| reasoning | 신뢰할 필드가 검증될 때까지 null | reasoning_output_tokens, output의 부분집합 |
| processedTokens | inputTotal + output | input_tokens + output_tokens |
| nativeTotal | Provider에 있으면 그대로 | total_tokens 등 실제 필드를 원형 보존 |

불변식과 예외:

- Codex cached ≤ input, reasoning ≤ output를 확인한다. 위반 시 음수를 clamp하지 않고 해당 projection을 invalid로 표시한다.
- Claude cacheWrite와 TTL breakdown을 동시에 더하지 않는다. breakdown 합이 total과 다르면 mismatch 진단과 비용 추정 불가 상태를 남긴다.
- Codex nonzero cache write의 포함 관계는 연구 게이트다. `processedTokens`에 추가로 더하지 않으며 의미가 미확인인 metric은 provisional로 표시한다.
- total-only Codex 레코드는 nativeTotal만 보존한다. `input=0, output=0`이라는 완전한 측정으로 바꾸지 않는다.
- 어떤 호출에서 값이 null이면 Turn의 완전 합계도 null이다. 알려진 부분합은 `knownSubtotal`과 `unknownObservationCount`로 따로 제공한다.
- `maxObservedInput`은 단일 호출의 최대 inputTotal이다. 실제 context window 점유율이나 턴 합계가 아니다. 호출별 관측이 없으면 null이다.
- `apiCallCount`는 안정된 호출 ID로 셀 수 있을 때만 제공한다. 누적 턴 레코드 개수를 호출 수로 쓰지 않는다.

검토 의견이 보고한 필드 관계를 출발점으로 삼되 fixture 검증 이전에는 지원 계약으로 확정하지 않는다.

### Claude identity와 revision

1. 동일 세션/원실행의 record UUID 복사본을 파일 경계 너머에서 제거한다.
2. requestId와 message.id의 alias 관계를 구축한다. requestId 유무가 줄마다 다르다고 두 호출로 나누지 않는다.
3. 기본 logical key는 Provider + origin session/execution + request identity다. requestId 아래 복수 message가 발견되면 schema evidence로 단위를 먼저 검증하고 임의 합산하지 않는다.
4. 스트리밍의 동일 응답은 compatible usage에 대해 output 최댓값을 가진 완전한 snapshot 하나를 선택한다. 동률은 검증된 event 순서로 결정한다.
5. 입력·모델 등 불변 필드가 충돌하면 서로 다른 줄의 최댓값을 섞어 가짜 usage를 만들지 않는다. conflict로 격리한다.
6. promptId와 parent metadata로 Turn 귀속. tool_result와 task-notification은 새 사용자 Turn으로 만들지 않는다.
7. ID 누락·부모 불명·순서 불명은 미귀속 또는 provisional이다. 가장 가까운 프롬프트로 자동 배정하지 않는다.

### Codex authority와 누적값

Adapter는 usageKind를 `call_delta`, `turn_snapshot`, `session_snapshot`으로 구분해야 한다. 필드 이름만 보고 delta라고 가정하지 않는다.

- 같은 실행의 turn snapshot은 revision을 갱신하고 합산하지 않는다.
- 호출별 delta와 turn snapshot이 둘 다 있으면 turn snapshot을 집계 authority로 쓰고 delta는 검증/상세용으로 둔다. 둘을 함께 더하지 않는다.
- root snapshot이 main-only인지 child-inclusive인지 지원 버전별 fixture로 확정한다. main-only이면 고유 child를 더하고, inclusive이면 포함된 child를 다시 더하지 않는다. 범위를 알 수 없으면 root 합계의 completeness를 보장하지 않는다.
- session cumulative만 있는 구형 형식은 인접 차감으로 Turn을 추정하지 않는다. 검증된 reset/동시 실행 규칙이 없으면 session-only 진단을 제공한다.
- 재개·fork 복제는 원실행 identity를 유지한다. origin lineage가 없으면 파일 간 무조건 중복 제거 또는 합산하지 않고 모호성으로 남긴다.

## Interfaces

```text
Discover(selector) -> SessionCandidates + Diagnostics
Read(snapshot) -> Observations + SourceCompleteness
Resolve(observations, lineage) -> Executions + Membership + Unattributed
Normalize(execution, semanticsVersion) -> UsageProjection + Diagnostics
Project(rootTurnKey) -> TurnSnapshot
Commit(snapshot, expectedRevision) -> Committed | Retry | Rejected
Render(snapshot, format) -> Text | VersionedJson
```

CLI exit code 제안: 0 정상 관측(부분 결과는 payload에 품질 표시), 1 내부/IO 실패, 2 잘못된 CLI 인자, 3 관측 결과 없음, 4 모호한 세션, 5 미지원 schema. `--strict`는 partial/invalid 결과에 6을 반환한다. **Hook entrypoint는 이 CLI 코드와 별개로 실패를 흡수한다.**

Hook 입력은 provider별 typed DTO로 받고 payload 크기 제한·path 검증을 적용한다. 이벤트와 버전별 neutral response 계약을 검증한다. 특히 Codex `Stop`/`SubagentStop`은 성공 stdout에 JSON을 요구하므로 일반 CLI 텍스트를 출력하면 안 된다. 필요한 neutral JSON을 사용하고 block/continue 제어 필드를 보내지 않는다. [Codex Hook 문서](https://learn.chatgpt.com/docs/hooks)

## External Dependencies / 근거와 검증 수준

| 근거 | 이번 준비에서 확인한 범위 | 남은 검증 |
|---|---|---|
| [Claude Hooks](https://code.claude.com/docs/en/hooks) | prompt_id, command Hook, async, 종료 코드 계약 | Hook ID와 transcript promptId 실제 대응, Esc·늦은 child 시나리오 |
| [Codex Hooks](https://learn.chatgpt.com/docs/hooks) | turn_id, Interrupt, async, Stop JSON 출력 | 설치된 앱/CLI 버전의 지원, 실제 rollout 형식과 귀속 |
| [Claude Monitoring](https://code.claude.com/docs/en/monitoring-usage) | prompt 식별자 및 query_source로 관측 계층 구분 가능 | api_request의 실제 scope와 독립 합계 비교 가능성 |
| 아이디어의 Claude 실측 | 2.1.277, transcript 275개/child 66개 분석 보고 | 이번 실행에서는 사용자 로그를 재수집하지 않음 |
| 아이디어의 Codex 실측 | 0.153.4 rollout 566개에 관한 보고 | turn snapshot 의미, 예외 5턴·total-only 원인 |
| session-report / ccusage | 검토 문서가 제안한 참조 도구 | 버전·라이선스·입력 범위 고정 후 독립 oracle로 검증 |

공식 문서 확인일은 2026-09-19다. OTel의 metric `query_source`와 API event의 query_source 값은 같은 vocabulary라고 가정하지 않는다. 참조 도구 코드를 복사하거나 runtime dependency로 추가하기 전에 라이선스를 확인한다. 실행에 외부 서비스/API 키는 필요하지 않은 설계다.

## State Management

execution state: `unknown → running → completed | interrupted | failed`. 명시적 종료 증거가 없으면 단순 시간 경과로 completed로 바꾸지 않는다.

measurement quality: `observed | provisional | partial | invalid | unsupported`. 실행이 completed여도 늦은 child·손상 로그가 있으면 partial/provisional일 수 있다. source가 사라진 보관값에는 별도의 `sourceAvailability=missing`을 붙인다.

재집계는 종전 Turn revision을 대체할 수 있다. 완전한 source manifest와 호환 parser로 재처리한 경우에만 잘못된 과거 projection을 교정한다. 파일 손실·truncate·권한 실패는 기존 정상값의 삭제 근거가 아니다. 원본이 없는 rebuild는 보관값을 그대로 두고 재검증 불가를 보고한다.

### SQLite 추천안

WAL, foreign keys, unique keys, schema migration을 사용한다. 긴 파일 읽기는 transaction 밖에서 처리하고, 짧은 commit만 직렬화한다. busy timeout과 최대 재시도 시간을 제한하고 초과 시 다음 조회/sync에서 복구한다. WAL은 네트워크 filesystem에 사용하지 않고 로컬 디스크만 지원한다. [SQLite WAL 문서](https://www.sqlite.org/wal.html)

원본 snapshot의 해시/extent와 expected revision으로 오래된 writer를 거부한다. 같은 source를 8개 worker가 처리해도 logical execution과 membership은 한 번만 저장되어야 한다. migration 전 backup을 만들고 중간 실패 시 이전 schema로 실행하지 않는다.

## Error Handling

| 오류 | 처리 |
|---|---|
| source 미발견 | 보관값 있으면 stale 표시, 없으면 no-data |
| 마지막 미완성 행 | 보류 후 provisional, 재조회 시 복구 |
| 중간 JSON 손상 | 해당 행 제외, 부분집계와 손상행 수 공개 |
| 정수 overflow / 음수 | invalid, 잘못된 수치 저장 금지 |
| 미지원 스키마 | unsupported, 0건 성공으로 위장 금지 |
| 저장소 잠금·디스크 부족 | 보관 실패 진단, 이전 ledger 유지 |
| Hook 파싱/실행 오류 | 비차단 neutral 종료, 추가 LLM 호출 없음 |
| 완전 source가 뒤늦게 도착 | 기존 root revision 갱신, child 중복 삽입 방지 |

Hook의 최상위 catch만으로 executable missing/timeout 같은 launch 실패까지 해결했다고 주장하지 않는다. 각 Provider에서 설치 검증으로 비차단 동작을 확인하고 설치된 entrypoint를 사용한 smoke test를 제공한다.

## Logging

JSON 진단에는 code, provider, 익명화/로컬 session reference, source index, line number, count, duration만 기록한다. 원본 행이나 예외의 민감한 문자열을 그대로 남기지 않는다. 회전 크기·보관 기간은 UC-007에서 확정한다. 일반 조회는 stderr, Hook은 로컬 진단 파일로 보내며 stdout은 Provider 계약 전용이다.

## Configuration

우선순위는 CLI 인자 > `TOKEN_METER_*` 환경변수 > 사용자 설정 > 기본값이다. 제품 환경변수 예: `TOKEN_METER_DATA_DIR`, `TOKEN_METER_LABEL`. 레포의 설정 파일은 자동 실행 코드나 명령을 제공할 수 없다.

추천 Windows data root는 `%LOCALAPPDATA%/TaskTokenMeter/`이며 UC-003 승인 전 확정값이 아니다. workspace-local 선택 시 `.token-meter/` 제외 규칙을 사용자에게 제시하고 기존 .gitignore를 덮어쓰지 않는다. Hook 설정도 기존 항목을 읽고 자신의 식별 가능한 항목만 병합/제거한다. 이번 단계에서 사용자 설정을 변경하지 않는다.

## Security Considerations

- prompt, message content, tool input, 환경변수 전체, .env 및 credential 저장 금지.
- 원본 로그는 읽기 전용; fixture는 합성 데이터를 기본으로 하고 실제 자료는 비식별화 검토 후에만 commit.
- Hook 인자를 shell 문자열로 이어 붙이지 않고 정적 executable + 안전한 argument 전달 사용.
- source root allowlist, 파일 크기/행 길이 제한, 경로 traversal와 junction 검증.
- 계측 자체에 네트워크 호출·LLM 호출 없음. OTel을 원격으로 활성화하는 기능은 후속 별도 범위.
- 로컬 DB는 암호화를 가정하지 않는다. 사용자 전용 filesystem 권한과 명시적 보관·삭제 정책을 제공한다.

## Testing Strategy

| Fixture | 검증할 핵심 |
|---|---|
| 두 Provider의 2턴 | 세션 누적과 Turn 차이, 경계별 정확 수치 |
| Claude 동일 request output 9→9→338 | 338 한 번, input/캐시 중복 없음 |
| requestId 유무 혼재·재개 UUID 복제 | alias 기반 같은 호출 유지 |
| Codex input=1000 cached=600 output=200 reasoning=50 | fresh=400, processed=1200 |
| Codex turn snapshot 100→250, delta도 존재 | 최종 250 한 번; delta와 중복 합산 없음 |
| main-only / child-inclusive root | 두 의미 모두 같은 실제 고유 실행 합계 |
| total-only, nonzero cache-write, 모순 필드 | null/invalid 구분, 의미 미확인 경고 |
| 늦은 child·귀속 실패·다른 workspace | root 수정/미귀속 유지, 근거 없는 귀속 없음 |
| 중간 손상·tail·truncate·권한 거부 | partial/stale, 정상 보관값 보존 |
| 여러 모델·fork·중단 | provenance 보존, 복사 소비량 재청구 없음 |
| concurrent writer / crash / migration | transaction 무결성, stale writer 방지 |
| Hook timeout·설정 기존 항목·설치 제거 | Provider 진행 유지, 기존 설정 보존 |

coverage는 동일 기간·모델·metric·실행 scope의 독립 reference가 있을 때 metric별 `observed/reference`로만 계산한다. denominator 0, 시점 불일치, reference 부재는 N/A다. 100% 초과를 clamp하지 않고 scope mismatch로 진단한다. 세션 coverage를 Turn coverage로 복사하지 않는다.

## Build / Deployment

UC-002 승인 후 `.sln`/`.csproj`, SDK pin, dependency lock, reproducible build와 테스트 명령을 만든다. 첫 Windows 지원 RID를 확정하고 개발 runtime이 없는 깨끗한 환경에서 설치→명령→Hook→제거를 확인한다. 설치 경로에 공백/한글을 포함한다. Native AOT는 필요성이 측정되기 전 기본값으로 정하지 않는다.

원격 publish, GitHub Release, 실제 사용자 Hook 설치는 구현·검증 결과를 갖춘 뒤 명시적 요청에 따라 수행한다.

## Technical Risks

가장 큰 위험은 내부 로그 계약의 변화와 관측 범위의 혼동이다. Provider 버전별 capability table을 먼저 만들고 알 수 없는 레코드를 조용히 무시하지 않는 방식으로 완화한다. 다음은 root inclusive semantics, 재개/fork identity, 원본 정리, concurrent commit이다. 모두 계획의 연구·fixture·저장소 테스트에 대응시킨다.

증분 파싱은 성능 예산을 실제로 초과할 때 도입한다. 그때도 inode/file identity, byte offset, tail buffer, parser version, truncate 감지와 full-rescan fallback을 하나의 변경으로 검증해야 한다.
