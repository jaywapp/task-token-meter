# Provider 계약과 관측 한계

조사일: 2026-09-20
대상 설치 버전: Claude Code `2.1.278`, Codex CLI `0.153.4`

## 목적과 판정 기준

이 문서는 내부 JSONL을 공개 API로 승격하지 않는다. 현재 설치 버전에서 구현 가능한 parser capability와, 근거가 부족해 실패로 처리해야 하는 경계를 고정한다. 원문 대화·도구 인자·식별자·개인 경로는 조사하거나 기록하지 않았고, 로컬 표본에서는 필드명·개수·숫자 관계만 집계했다.

판정은 다음 네 가지다.

| 상태 | 의미 | 구현 처리 |
|---|---|---|
| `documented` | Provider 공식 문서가 현재 동작을 명시 | 문서가 지정한 최소 버전과 이벤트에 한해 계약으로 사용 |
| `observed` | 명시된 설치 버전의 로컬 구조·수치 표본으로 재현 | 정확한 버전·record shape capability에만 활성화 |
| `unsupported` | identity 또는 snapshot 의미를 결정할 증거가 부족 | 성공 0건으로 처리하지 않고 unsupported 진단 |
| `unknown` | 반례가 없거나 추가 재현이 필요 | native 값은 보존하되 완전 합계에서 제외하거나 partial 처리 |

근거 우선순위는 공식 문서, 설치된 실행 파일/플러그인 메타데이터, 최소 로컬 구조 측정, 아이디어 문서의 이전 측정 순이다. Claude transcript와 Codex rollout 형식은 공식 안정 API가 아니므로 공식 Hook/App Server 문서가 내부 필드 의미까지 보장한다고 해석하지 않는다.

## 지원 기준 요약

| Provider | 버전/형식 | 판정 | 활성화할 capability |
|---|---|---|---|
| Claude Code | `2.1.278`, 조사 표본과 같은 assistant usage shape | `observed` | request/message alias, compatible snapshot 선택, entry UUID 중복 제거, `promptId` 기반 Turn, TTL cache-write breakdown, 귀속 가능한 subagent 포함 |
| Claude Code Hook | `2.1.278` | `documented` | `session_id`, `prompt_id`, Stop/SubagentStop, `async`; Hook은 session 재집계 트리거로만 사용 |
| Codex CLI | `0.153.4`, `token_usage_record` shape | `observed` | call delta, turn/thread snapshot 구분, `session_id` root lineage, main-only root snapshot, child 별도 합산 |
| Codex Hook | 설치된 `0.153.4`와 현재 공식 문서 | `documented` + 설치 smoke 필요 | `session_id`, `turn_id`, Stop/SubagentStop/Interrupt, 이벤트별 stdout 형식 |
| 그 밖의 버전/shape | 모두 | `unsupported` | fixture나 명시적 capability probe가 추가되기 전 자동 추정 금지 |

버전 문자열만으로 형식을 확정하지 않는다. Adapter는 필수 필드 집합과 불변식을 함께 검사하고, 일치하지 않으면 해당 capability를 끈다.

## Claude Code 계약

### Turn identity: `prompt_id`와 `promptId`

- Claude Code `2.1.196+`의 Hook 공통 입력 `prompt_id`는 현재 처리 중인 사용자 prompt의 UUID이며 OpenTelemetry `prompt.id`와 일치한다. 첫 사용자 입력 전에는 없다. 이는 [Hooks reference](https://code.claude.com/docs/en/hooks#common-input-fields)의 `documented` 계약이다.
- OpenTelemetry `prompt.id`는 한 사용자 prompt에서 다음 prompt 전까지 발생한 API·도구 이벤트를 묶는다. resume without fork는 `session.id`를 유지하지만 process별 `event.sequence`는 재시작될 수 있다. 이는 [Monitoring 문서](https://code.claude.com/docs/en/monitoring-usage#event-correlation-attributes)의 `documented` 계약이다.
- 공식 문서는 Hook `prompt_id`가 transcript의 camelCase `promptId`와 같다고 명시하지 않는다. Monitoring 문서는 transcript join 자체를 버전별 내부 계약으로 취급한다. 따라서 `prompt_id == promptId` 직접 join은 `unknown`이다.
- 로컬 `2.1.278` 최근 transcript 100개에서 user 5,620행 중 5,603행에 `promptId`가 있었고, human 후보 4,399행 중 8행은 없었다. assistant usage 9,531행에는 `promptId`가 없었다. 따라서 Adapter는 user entry의 `promptId`로 Turn을 열고 parent/entry 순서로 그 뒤의 assistant 호출을 연결한다. assistant 행에 `promptId`가 있다고 요구하면 안 된다.

Hook은 Turn identity를 ledger에 직접 쓰지 않는다. `session_id`로 재집계를 요청하고 transcript가 가진 `promptId`를 parser authority로 사용한다. 이 방식은 두 ID의 직접 동일성 검증을 TASK-003 이후의 Hook smoke로 미룰 수 있다.

재현 절차:

1. 합성 transcript에서 promptId가 있는 user entry와 promptId가 없는 assistant usage entry를 순서대로 읽는다.
2. promptId가 없는 첫 human prompt, tool result, compact/meta entry를 각각 넣는다.
3. 실제 Hook smoke에서는 prompt 본문을 저장하지 않고 Hook `prompt_id`와 같은 시점 transcript user `promptId`의 equality boolean만 기록한다.
4. equality가 확인되지 않더라도 Hook은 session rescan만 요청하며 Turn을 생성하지 않는다.

### API 호출 identity와 snapshot revision

[Monitoring 문서](https://code.claude.com/docs/en/monitoring-usage#event-correlation-attributes)는 API event의 `request_id`가 transcript assistant entry의 `requestId`에 저장된다고 설명한다. `message.uuid`는 최종 transcript entry를 가리키며, raw API response index의 `message.id`/`message.uuid` 연결은 `2.1.274+`에 제공된다. 동시에 transcript 형식은 내부 구현이며 릴리스마다 바뀔 수 있다고 명시한다.

`2.1.278` 로컬 표본의 구조 관계는 다음과 같다.

| 항목 | 결과 | 판정 |
|---|---:|---|
| assistant usage 행 | 9,531 | 표본 범위 |
| `requestId` 우선, `message.id` fallback logical call | 4,336 | `observed` |
| 한 logical call의 복수 행 | 3,162 | `observed` |
| 행 사이 `output_tokens`가 달라진 call | 415 | `observed` |
| 같은 request/message에서 input·cache·model 충돌 | 0 | 반례 없음 |
| 같은 파일에서 output 감소 전이 | 0 | 반례 없음 |
| `requestId` 하나가 여러 `message.id`에 대응 | 0 | 반례 없음 |
| `message.id` 하나가 여러 `requestId`에 대응 | 0 | 반례 없음 |
| `requestId` 누락 usage 행 | 15 | message fallback 필요 |
| 여러 파일에 걸친 logical call | 347 | 파일 단위 dedupe만으로 부족 |
| 중복 entry UUID | 1,055 | global UUID dedupe 필요 |

지원 알고리즘은 다음 순서를 따른다.

1. 동일 origin lineage에서 entry `uuid`를 먼저 중복 제거한다.
2. `requestId`를 primary alias로, 유효한 `message.id`를 fallback alias로 사용한다.
3. 같은 alias의 snapshot은 model, input, cache read/write, TTL breakdown이 모두 compatible할 때만 한 호출로 합친다.
4. compatible snapshot 중 `output_tokens`가 가장 큰 완전 snapshot 하나를 선택한다. 동률이면 source의 검증된 행 순서에서 마지막 것을 쓴다.
5. immutable 필드가 충돌하면 필드별 최댓값을 섞지 않고 logical call을 `invalid_alias_conflict`로 격리한다.
6. 두 ID 모두 없으면 시간 근접으로 합치지 않고 unattributed observation으로 남긴다.

이 규칙은 설치된 `2.1.278`에서 `observed`이며 다른 버전의 일반 계약은 아니다. 공식 `session-report`도 requestId 우선, message id fallback, 최대 output 선택을 사용하지만 이는 참조 구현이지 Provider API가 아니다.

### Cache write와 output detail

`2.1.278` 표본의 usage 9,531행 모두 `cache_creation_input_tokens`와 `cache_creation.ephemeral_5m_input_tokens`, `ephemeral_1h_input_tokens`를 가졌다. TTL 두 필드의 합과 total cache creation이 다른 행은 0개였다. 7,215행에는 `output_tokens_details.thinking_tokens`가 있었다.

- `cache_creation_input_tokens`는 TTL breakdown과 함께 더하지 않는다.
- normalized cache write는 5m/1h를 각각 보존하고, total은 breakdown 합과 일치할 때만 제공한다.
- `thinking_tokens`가 `output_tokens`에 포함되는지와 가격 의미는 이번 조사에서 공식 transcript 계약으로 확인하지 못했다. native detail만 보존하고 normalized reasoning은 `unknown`으로 둔다.

### Subagent, task-notification, 늦은 child

공식 Hook 문서는 subagent transcript가 main transcript 아래의 별도 `subagents/` 위치에 있고, SubagentStop은 `agent_id`, `agent_type`, `agent_transcript_path`를 제공한다고 명시한다. Stop/SubagentStop의 `background_tasks`는 parent session 범위이며, main Stop 시 transcript가 최종 assistant message보다 늦을 수 있다.

로컬 표본에서는 subagent 파일 36개에 서로 다른 `promptId` 119개가 있었고 119개 모두 표본 main transcript의 promptId와 일치했다. subagent assistant usage 행 자체에는 promptId가 없었다. 따라서 child 파일의 user promptId와 parent session/agent metadata가 함께 확인될 때만 root Turn에 귀속한다.

`<task-notification...>` 형태의 user entry는 49행 관측됐다. 설치된 공식 `session-report` 분석기는 task notification, scheduled wakeup, background task를 새 human prompt로 만들지 않고 직전 active prompt에 계속 귀속한다. 이는 `session-report`의 분석 정책이며 Provider의 안정 계약은 아니다. 제품은 다음처럼 처리한다.

- 명시적 promptId가 있으면 그 ID를 우선한다.
- notification marker만 있고 identity가 없으면 새 Turn을 만들지 않으며 이전 Turn 귀속은 `provisional`로 표시한다.
- Stop에 background task가 남아 있거나 child source가 아직 쓰이는 중이면 root를 terminal execution으로 표시할 수 있어도 measurement는 provisional이다.
- SubagentStop, 다음 조회, 다음 session sync에서 같은 root key를 재집계한다.

시간 근접만으로 늦은 child를 이전 Turn에 붙이지 않는다.

### Resume, fork, root session lineage

[Hooks reference](https://code.claude.com/docs/en/hooks#sessionstart)는 SessionStart source를 `startup`, `resume`, `clear`, `compact`, `fork`로 구분한다. fork source는 `2.1.214+`이며 그 전 버전은 `resume`으로 보고했다. [Monitoring 문서](https://code.claude.com/docs/en/monitoring-usage#event-correlation-attributes)는 fork하지 않은 resume이 같은 `session.id`를 유지한다고 명시한다.

Hook payload에는 fork parent session id가 없다. transcript parent lineage도 공개 계약이 아니다. 설치된 `session-report`는 resume/fork에서 재직렬화된 entry를 global UUID로 제거하고, parent 파일을 child/fork 파일보다 먼저 처리한다. 따라서:

- resume은 같은 session id와 entry UUID 중복을 제거한다.
- fork가 복사한 과거 entry는 같은 UUID가 확인될 때만 새 소비에서 제외한다.
- fork parent session을 안정적으로 찾지 못하면 별도 session으로 보존하고 `fork_parent_unknown`을 낸다.
- 서로 다른 origin에서 우연히 같은 requestId가 나왔다고 global dedupe하지 않는다. session lineage와 entry UUID가 필요하다.

fork parent identity를 Hook만으로 복원하는 기능은 `unsupported`다. 합성 fixture와 승인된 smoke에서 명시적 parent metadata가 확인되기 전에는 root session을 추정하지 않는다.

### Interrupt와 Hook stdout

Claude Code의 Stop은 사용자가 중단한 경우 실행되지 않고 API 오류는 StopFailure를 발생시킨다. 현재 공식 Hook 목록에는 main-turn Interrupt 이벤트가 없다. 따라서 Stop 누락을 completed로 해석하지 않는다. transcript에 검증된 중단 표식이 없으면 execution state는 `unknown` 또는 `running`, measurement는 provisional이다.

Hook entrypoint 계약은 다음과 같다.

- 가장 안전한 neutral command response는 exit code `0`과 빈 stdout이다.
- `UserPromptSubmit`, `SessionStart` 등 일부 이벤트는 plain stdout을 모델 context에 넣으므로 Meter Hook은 stdout에 진단을 쓰지 않는다.
- Stop에서 exit code `2` 또는 block JSON은 모델을 계속 실행시켜 새 usage를 만들 수 있으므로 절대 사용하지 않는다.
- `async: true` command Hook은 Claude를 막지 못한다. 완료 stdout의 `additionalContext`/`systemMessage`는 다음 Turn에 전달될 수 있으므로 빈 stdout을 유지한다.
- 현재 문서상 일반 async Hook은 시작된 뒤 `timeout`을 적용받지 않으며 `claude -p` 종료 시 남은 process가 취소될 수 있다. 장기 집계 대신 빠른 enqueue/rescan entrypoint만 둔다.
- 실행 파일 누락, 시작 실패, non-2 exit은 Provider 진행을 막지 않아도 로컬 진단으로 남겨야 한다. E2E에서 실제 설치 상태로 확인하기 전까지 fail-open을 완료했다고 보지 않는다.

## Codex 계약

### 공개 Hook/App Server 경계

[Codex Hooks 문서](https://developers.openai.com/codex/hooks)는 Hook 공통 `session_id`를 현재 Codex session ID로 정의하고, subagent Hook에는 parent session ID를 준다. Turn-scoped 이벤트는 `turn_id`를 제공한다. `root_turn_id`는 공개 Hook 계약에 없다. transcript path 형식도 안정 Hook 인터페이스가 아니라고 명시한다.

[Codex App Server 문서](https://developers.openai.com/codex/app-server)는 `thread/start`, `thread/resume`, `thread/fork`, `turn/start`, `turn/interrupt`, `turn/completed`를 공개한다. fork 결과에는 새 thread id와 기존 `sessionId`, `forkedFromId`가 있으며 turn 완료 상태는 `completed`, `interrupted`, `failed`다. `thread/tokenUsage/updated` 알림은 문서화되어 있지만 내부 rollout의 세 usage 객체와 동일한 의미라고 명시되지는 않는다.

따라서 Hook/App Server의 public identity와 rollout usage semantics는 별도 capability로 관리한다.

### `token_usage_record`의 세 usage kind

Codex CLI `0.153.4` 최근 rollout 100개의 lineage 측정 snapshot에서 `token_usage_record` 2,470개를 관측했다. 세 객체 모두 다음 필드를 가졌다. 아래 invariant 측정은 쓰기 중인 rollout을 다시 연 별도 snapshot(2,457 record / 7,371 usage object)이므로 두 표본 수는 직접 합산하지 않는다.

`input_tokens`, `cached_input_tokens`, `output_tokens`, `reasoning_output_tokens`, `total_tokens`, `cache_write_input_tokens`

Adapter 명칭은 다음처럼 고정한다.

| rollout 필드 | usage kind | `0.153.4` 의미 | 집계 authority |
|---|---|---|---|
| `usage` | `call_delta` | 바로 이전 snapshot에 더해지는 단일 호출 delta | 상세·교차 검증 |
| `turn_token_usage` | `turn_snapshot` | 해당 `turn_id`의 누적 snapshot | Turn 합계 authority |
| `thread_token_usage` | `session_snapshot` | 해당 `thread_id`의 여러 Turn 누적 snapshot | session/thread 진단, Turn 합계에 더하지 않음 |

재현 결과:

- 연속 turn snapshot 1,402전이는 `previous + usage == current`를 모두 만족했다.
- 연속 thread snapshot 2,373전이도 같은 관계를 모두 만족했다.
- Turn 1,061개 중 1,056개는 최종 turn snapshot과 call delta 합이 모든 metric에서 같았다. 5개는 달랐다.

따라서 `turn_snapshot`을 authority로 사용하되 delta 합과 다르면 snapshot을 버리지 않고 `delta_snapshot_mismatch`와 partial 품질을 남긴다. delta와 snapshot을 함께 더하지 않는다. 최종 snapshot은 행 순서상 마지막 revision이며 단순 합산하지 않는다. 5개 예외의 원인은 표본 시작 전 호출, 복제, 유실 중 어느 것인지 이번 조사에서 확정하지 못했다.

### Root turn과 root session lineage

같은 표본에서 token usage record의 ID 관계는 다음과 같다.

| 관계 | 결과 |
|---|---:|
| `root_turn_id == turn_id` | 1,112 |
| `root_turn_id != turn_id` | 1,345 |
| `session_id == thread_id` | 1,112 |
| `session_id != thread_id` | 1,345 |
| 누락된 네 ID | 0 |

파일 100개 각각의 `session_meta`와 usage를 비교하면 `thread_id == session_meta.id`, `session_id == session_meta.session_id`가 2,470건 모두 성립했다. child session meta 89개 모두 `parent_thread_id`를 가졌지만 23개는 root `session_id`와 immediate parent가 달랐다. 즉 nested child에서 immediate parent와 root session은 다르다.

구현 계약:

- root Turn key는 `(provider, payload.session_id, payload.root_turn_id)`다.
- 실행 identity는 `(payload.thread_id, payload.turn_id, response_id 또는 검증된 call identity)`다.
- `parent_thread_id`는 immediate parent edge이고 `session_id`는 관측된 root session이다.
- `root_turn_id`만으로 root session을 만들지 않는다.
- `session_meta`/record가 위 관계를 위반하면 root 귀속을 중단하고 unattributed로 보존한다.

공식 App Server fork와 rollout `forked_from_id`는 별개 source다. `session_meta.forked_from_id`는 표본 5개에서 존재했지만 복사된 history의 원실행 identity 규칙은 공식 문서가 보장하지 않는다. fork replay dedupe는 `unknown`이며 fixture에서 origin identity를 증명하지 못하면 합산도 제거도 하지 않는다.

### Root snapshot의 child 포함 범위

child가 있는 root group 46개 중 root main snapshot을 같은 표본에서 함께 읽을 수 있었던 44개를 비교했다.

- root final turn snapshot == root thread의 call delta 합: 44
- root final turn snapshot == root + child 전체 call delta 합: 0
- 둘 다 아닌 경우: 0
- 표본에 root source가 없어 판정 불가: 2

따라서 설치된 `0.153.4`/현재 record shape의 `rootScope` capability는 `main-only`다. root turn snapshot에 귀속 가능한 child Turn의 authoritative snapshot을 실행 identity 합집합으로 한 번씩 추가한다. 이 결과를 다른 버전에 일반화하지 않는다. 다른 버전 또는 shape는 `rootScope=unknown`으로 시작하며 root와 child를 자동 합산하지 않고 partial을 반환한다.

### 포함 관계, cache write, total-only

표본의 call/turn/thread usage 7,371개 객체 모두 다음을 만족했다.

- `cached_input_tokens <= input_tokens`
- `reasoning_output_tokens <= output_tokens`
- `total_tokens == input_tokens + output_tokens`

`cache_write_input_tokens`가 0보다 큰 객체와 total-only 객체는 이번 표본에서 각각 0개였다. 아이디어 문서의 같은 Codex 버전 이전 표본에는 total-only 14건이 보고됐지만 이번 조사에서는 재현되지 않았다.

- `input_tokens`는 cached input을 포함하므로 fresh input은 두 값이 모두 있을 때만 차감한다.
- reasoning은 output의 부분집합으로만 표시한다.
- cache write는 native에 보존하되 `input_tokens`와의 포함 관계가 확인되지 않았으므로 processed에 추가하지 않는다. nonzero가 나타나면 measurement를 provisional로 둔다.
- total-only는 native total만 보존한다. input/output 0으로 정규화하지 않는다.
- 누락 ID, 음수, 부분집합 위반, total 불변식 위반은 해당 projection을 invalid 또는 unsupported로 만든다.

### Interrupt, 늦은 child, Hook stdout

Codex 공식 문서에서 Interrupt는 main thread의 active Turn을 중단할 때 실행되고 subagent에는 실행되지 않는다. `turn/completed`는 interrupt 뒤 `interrupted` 상태를 전달한다. rollout 표본에도 `turn_aborted` record가 있었지만 Hook와 rollout status의 직접 join은 별도 smoke가 필요하다.

- main Interrupt는 `turn_id`로 같은 root Turn 재집계를 요청한다.
- subagent에는 Interrupt Hook이 없으므로 child terminal state를 Hook만으로 확정하지 않는다.
- Stop 뒤 child가 늦게 끝날 수 있으므로 SubagentStop과 다음 조회에서 root revision을 갱신한다.
- child source가 없거나 진행 중이면 root usage는 partial/provisional이다.

stdout은 이벤트별로 다르다.

| 이벤트 | neutral stdout |
|---|---|
| `Stop`, `SubagentStop` | exit 0 + `{}` JSON. plain text는 invalid이며 block/continue 필드를 보내지 않음 |
| `Interrupt` | exit 0 + 빈 stdout. JSON을 쓰면 `systemMessage` 없는 `{}`만 허용 |
| 기타 측정 Hook | 공식 이벤트별 규칙을 fixture로 고정; context를 추가하는 필드 금지 |

Codex async Hook output은 다음 안전 지점 또는 다음 사용자 Turn에 전달될 수 있고 session 종료 시 미완료 background Hook은 취소된다. Meter는 stdout에 `additionalContext`, `systemMessage`, `decision`, `continue`를 쓰지 않는다. Stop의 block 응답은 continuation prompt를 만들어 토큰을 더 쓰므로 금지한다.

## 참조 도구 계약

### Anthropic 공식 `session-report`

- 설치 메타데이터: version은 `unknown`, commit `c447c3207a425bc4e2a0d068435f64b0477ae981`, 마지막 갱신 2026-09-19.
- 라이선스: plugin 디렉터리의 Apache License 2.0.
- 입력 범위: Claude local project transcript와 nested subagent transcript. 기본 보고 기간은 7일이며 `--since` 또는 all-time으로 바뀐다.
- 분석 범위: project/session/subagent/skill, cache break, prompt별 상위 usage. requestId/message fallback, max output snapshot, global entry UUID dedupe, task-notification의 이전 prompt 귀속을 구현한다.
- 한계: prompt preview를 산출할 수 있고 내부 background fork 일부를 prompt에 귀속하지 않는다. Codex는 다루지 않는다. 이 도구의 출력 파일을 fixture나 repository에 복사하지 않는다.

제품 코드 복사의 전제는 Apache-2.0 고지와 변경 표시 검토다. MVP에서는 runtime dependency 대신 Claude parser의 독립 검증 참고값으로만 사용한다.

### `ccusage`

- 설치 버전: `20.0.18`.
- 라이선스: upstream tag `v20.0.18`의 MIT.
- 범위: Claude와 Codex를 포함한 local agent data를 일/주/월/session 단위로 집계한다. 설치된 도움말에는 Claude `daily/monthly/weekly/session/blocks`, Codex `daily/monthly/session`이 있고 prompt/Turn report는 없다.
- 한계: Task Token Meter와 group key·시간 경계·child/fork scope가 다르다. 비용은 pricing source까지 포함하므로 토큰 oracle과 분리한다. `--offline --no-cost --json`을 사용해도 같은 source extent·model·metric·child 범위가 확인될 때만 session total 비교에 쓴다.

이번 TASK에서는 ccusage report나 session-report report를 실행하지 않았다. 따라서 두 도구와의 숫자 일치는 아직 검증 결과가 아니다.

## 구현 capability 요구사항

Adapter는 최소한 다음 capability를 보고해야 한다.

```text
providerVersion
recordShapeVersion
turnIdentity: transcript-prompt-id | rollout-turn-id | unsupported
callIdentity: request-message-alias | response-id | unsupported
usageKinds: call-delta, turn-snapshot, session-snapshot
turnSnapshotAuthority: true | false | unknown
rootSessionSource: record-session-id | lineage | unknown
rootScope: main-only | child-inclusive | unknown
cacheWriteSemantics: ttl-breakdown | included-unknown | absent | unknown
interruptEvidence: hook | log | none
forkReplayIdentity: entry-uuid | origin-id | unknown
hookNeutralResponseVersion
```

Parser는 capability와 실제 필드 검증이 모두 통과한 경우에만 projection을 만든다. `unknown`을 false나 0으로 바꾸지 않는다.

## Golden fixture 요구사항

TASK-004는 다음 합성 fixture와 기대 진단을 반드시 포함해야 한다.

### Claude

1. requestId/message.id가 같은 `output 9 → 9 → 338`: 338 snapshot 한 번.
2. requestId가 중간 행에서 누락되지만 message.id가 같은 alias: 한 호출.
3. 같은 alias에서 input/model 충돌: `invalid_alias_conflict`, 필드별 max 금지.
4. 파일 사이 UUID replay와 logical call replay: 한 번만 집계.
5. human user에 promptId 없음, tool result/meta/compact: 새 Turn 자동 생성 금지.
6. main promptId와 child user promptId 일치, child assistant에는 promptId 없음: child를 한 번 귀속.
7. task-notification과 늦은 child: root key 유지, provisional에서 새 revision으로 갱신.
8. resume same session과 fork new session replay: UUID 증거가 있는 복사만 제외.
9. 5m/1h breakdown 정상·불일치, thinking detail 존재: cache total 중복 금지, reasoning unknown.
10. user interrupt로 Stop 없음, StopFailure, transcript tail lag: completed 오판 금지.

### Codex

1. call delta와 turn snapshot `100 → 250`: final 250 한 번, delta와 중복 합산 금지.
2. thread/session snapshot이 여러 Turn에 누적: Turn 합계에 thread snapshot을 더하지 않음.
3. root main-only + child 두 개: 고유 child snapshot을 한 번씩 추가.
4. child-inclusive 합성 capability: child를 다시 더하지 않음.
5. `rootScope=unknown`: 전체 root 합계 partial, 임의 합산 금지.
6. nested child에서 root session과 immediate parent가 다름: `session_id` root 유지.
7. turn snapshot과 delta 합 mismatch: snapshot authority + mismatch 진단.
8. total-only, nonzero cache write, 누락 ID, cached>input, reasoning>output, total mismatch: unknown/invalid 구별.
9. forked session replay에 origin evidence 있음/없음: 각각 dedupe/ambiguous.
10. main Interrupt, child 무-Interrupt, 늦은 SubagentStop: root revision 갱신.
11. Stop/SubagentStop `{}`, Interrupt 빈 stdout, plain text와 block 응답: neutral 계약과 금지 동작 검증.

각 fixture는 Provider version, record shape, 합성 여부, 기대 native usage, normalized usage, membership, quality, diagnostic code를 함께 기록해야 한다.

## 남은 unsupported/unknown

- Claude Hook `prompt_id`와 transcript `promptId`의 실제 equality.
- Claude fork parent session identity와 copied history의 공개 lineage.
- Claude transcript `output_tokens_details.thinking_tokens`의 안정 의미와 포함 관계.
- notification marker를 직전 prompt에 귀속하는 정책의 Provider 보장.
- Codex 1,061개 Turn 중 delta 합과 snapshot이 달랐던 5개의 원인.
- Codex nonzero `cache_write_input_tokens`의 `input_tokens` 포함 관계.
- Codex total-only record의 현재 재현 조건.
- Codex 다른 버전에서 root snapshot이 main-only인지 child-inclusive인지.
- 두 Provider에서 Hook ID와 최종 persisted record가 같은 Turn을 가리키는 E2E equality.
- session-report/ccusage와 동일 scope의 숫자 대조.

이 항목은 TASK-004 fixture 또는 이후 승인된 smoke 없이 지원으로 바꾸지 않는다.
