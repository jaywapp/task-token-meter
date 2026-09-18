---
title: Task Token Meter 아이디어 검토 — Claude 의견
category: idea
tags:
  - ai
  - claude-code
  - codex
  - token
  - observability
  - review
source: https://github.com/jaywapp/wiki/blob/develop/idea/task-token-meter.md
updated: 2026-09-19
---

# Task Token Meter 아이디어 검토 — Claude 의견

> 만들 가치가 있고 원칙도 맞다. 다만 설계의 중심을 **"Hook이 경계를 잡는다"에서 "로그가 진실이고 Hook은 트리거일 뿐"으로** 옮겨야 한다. Claude Code와 Codex 모두 로그에 이미 턴 ID가 있어서, MVP는 Hook 없이 시작할 수 있다.

- **검토 대상:** [Task Token Meter](./task-token-meter.md) ([wiki](https://github.com/jaywapp/wiki) `develop` 커밋 `caaf835` 기준)
- **검토자:** Claude (Opus 5), 2026-09-19
- **근거:** 이 PC의 실제 세션 로그를 스크립트로 분석했다(Claude Code 2.1.277 transcript 275개와 서브에이전트 transcript 66개, Codex 0.153.4 rollout 566개). 대화 내용은 보지 않고 구조와 수치만 뽑았다. 여기에 공식 문서를 더했다. 직접 확인한 사실과 추정은 [마지막 절](#확인한-사실과-측정이-필요한-추정)에서 구분한다.

## 결론

문제 정의("세션 누적이 아니라 작업별 소비량")와 원칙(LLM으로 세지 않기, 실제 Usage 우선, Native Usage 보존, 실패해도 작업 계속)은 그대로 가져가면 된다. 고칠 곳은 다섯 가지다.

1. **턴 경계를 Hook 상태로 잡으면 깨진다.** 로그의 턴 ID(Claude `promptId`, Codex `turn_id`)로 턴을 다시 만들고, Ledger는 언제든 다시 계산할 수 있는 파생 데이터로 둔다.
2. **서브에이전트와 부가 호출이 범위에 없다.** 서브에이전트를 많이 쓰는 작업일수록 비용이 체계적으로 적게 잡힌다.
3. **Normalized Usage 정의에 이중 계산 함정이 있다.** Codex의 `input_tokens`는 캐시분을 포함하고, reasoning은 output의 일부다.
4. **"Processed" 합계는 비교 지표로 쓸 수 없다.** 캐시 읽기는 단가가 1/10인데 합계의 90%를 차지한다.
5. **비교가 장기 목표라면 구성 정보를 MVP부터 남겨야 한다.** 나중에는 복원할 수 없다.

## 잘한 점

- 토큰 측정에 LLM을 쓰지 않고, Skill은 CLI를 부르는 역할로만 제한했다.
- Provider Native Usage를 보존하고 Normalized Usage를 따로 두었다. 아래 3번의 함정을 고려하면 꼭 필요한 결정이다.
- Claude JSONL을 단순 합산하면 안 된다고 명시했다. 실측해 보니 단순 합산은 약 2배로 과대 집계된다.
- Hook이 실패해도 AI 작업은 계속한다는 정책을 처음부터 넣었다.
- Task Grouping을 Later로 미루고 MVP를 Turn 측정으로 좁혔다.

## 로그 실측 결과

| 항목 | 결과 | 의미 |
|---|---|---|
| Claude 중복 기록 | assistant 줄 11,676개 중 실제 API 호출은 5,731개. 단순 합산하면 cache read 2.01배, output 2.24배 | 중복 제거는 필수다 |
| 같은 호출의 줄별 usage | 여러 줄로 나뉜 호출 3,774건 중 821건은 줄마다 usage가 다르다. output_tokens가 줄마다 커진다(예: 9 → 9 → 338) | **첫 줄을 쓰면 output이 약 7% 적게 잡힌다**(4.15M 대 4.46M). 마지막 줄(최댓값)을 써야 한다 |
| Claude 턴 ID | user 항목 6,923개 중 6,909개에 `promptId`가 있다. tool_result 항목의 99.4%가 해당 프롬프트와 같은 `promptId`를 가진다 | transcript 자체에 턴 키가 있다 |
| Claude 서브에이전트 | `<session>/subagents/*.jsonl` 66개 중 65개의 `promptId`가 부모 세션 프롬프트와 일치한다 | 서브에이전트 usage를 해당 턴에 넣을 수 있다 |
| Claude 캐시 쓰기 | 모든 usage에 `cache_creation.ephemeral_5m_input_tokens`와 `ephemeral_1h_input_tokens`가 나뉘어 있다 | 두 값은 단가가 다르므로 따로 보존한다 |
| Claude 호출 단위 속성 | assistant 항목에 `attributionSkill`, `attributionPlugin`, `attributionMcpServer`, `attributionMcpTool`, `effort`가 있다 | Skill·MCP별 비교를 따로 추론할 필요가 없다 |
| Claude 부가 호출 | Claude Code 자체 집계(`cost-state`)와 transcript 합계를 7개 세션에서 비교하면 입력은 84~99.9%, 출력은 88~99.8%만 일치한다. `cost-state`에는 haiku 모델이 있지만 transcript에는 대부분 없다 | 제목 생성 같은 부가 호출은 transcript에 남지 않는다 |
| Codex 턴 usage | `token_usage_record`에 `turn_id`, `root_turn_id`, `turn_token_usage`가 있다. 800턴 중 795턴에서 `turn_token_usage`가 호출별 usage의 합과 같다 | Codex는 턴별 합계를 이미 기록한다 |
| Codex 하위 스레드 | 레코드 1,954건 중 972건은 `root_turn_id`와 `turn_id`가 다르다 | usage 레코드의 절반 가까이가 하위 스레드에서 나온다. 루트 턴으로 귀속해야 한다 |
| Codex 필드 관계 | `cached_input_tokens ≤ input_tokens`, `reasoning_output_tokens ≤ output_tokens`가 1,381건 모두 성립한다. `total = input + output`은 1,367건에서 성립하고, 나머지 14건은 total만 있고 나머지 값이 0인 이상 레코드다 | 캐시와 reasoning은 **부분집합**이다 |
| Codex 캐시 쓰기 | `cache_write_input_tokens` 필드가 있지만 샘플에서는 전부 0이다 | 원문 7.5절의 "Codex Cached Write: 해당 없음"은 이제 맞지 않다 |
| 파싱 비용 | 가장 큰 transcript(17.6MB)를 Python으로 전부 파싱해도 약 0.16초. 프로세스 시작은 python 약 35ms, `powershell -NoProfile` 약 160ms | 증분 파싱은 아직 필요 없다 |

## 핵심 문제 (중요한 순)

### 1. 턴 경계는 Hook이 아니라 로그의 턴 ID로 정한다

원문은 `UserPromptSubmit`을 Turn Start, `Stop`을 Turn End로 보고 그 사이를 집계한다. 이 방식은 다음 경우에 깨진다.

- **Stop이 오지 않는 경우:** Codex 문서는 Esc로 중단하면 Stop 대신 별도의 Interrupt 이벤트가 온다고 밝힌다. Claude Code 문서에는 이 경우가 명시돼 있지 않다. 시작만 있고 끝이 없는 턴이 생긴다.
- **Stop 이후에도 토큰이 늘어나는 경우:** 백그라운드 서브에이전트나 명령이 끝나면 사용자 프롬프트 없이 모델이 다시 호출된다(task-notification). Anthropic 공식 `session-report` 플러그인도 이런 후속 호출을 원래 프롬프트에 합산한다.
- **Hook이 누락된 경우:** 설치 전 세션, 설정 오류, 타임아웃으로 빠진 턴은 영구히 사라진다.

두 Provider 모두 이미 턴 ID를 제공한다.

| | 로그 | Hook 입력 |
|---|---|---|
| Claude Code | user 항목의 `promptId` | 공통 필드 `prompt_id` |
| Codex | `turn_id`, 하위 스레드는 `root_turn_id` | `UserPromptSubmit`, `Stop` 등에 `turn_id` |

**권고**

- 턴은 `(session, promptId | turn_id)`로 식별한다.
- Ledger 레코드는 transcript에서 언제든 다시 계산할 수 있는 파생값으로 두고, 턴 키로 upsert한다.
- Hook은 "이 세션을 다시 집계하라"는 트리거로만 쓴다(`Stop`, `async: true`). `UserPromptSubmit` Hook은 필요 없다.
- 이렇게 하면 과거 세션을 소급 집계할 수 있고, 늦게 끝난 서브에이전트와 중단된 턴도 다음 집계 때 정리된다.
- 따라서 **MVP 첫 버전은 Hook 없이** `token-meter last`가 조회 시점에 transcript를 읽는 방식으로 충분하다. Hook은 장기 보관용 Ledger를 쓸 때 붙인다.

### 2. 서브에이전트와 부가 호출이 측정 범위에서 빠져 있다

- **Claude 서브에이전트:** usage가 main transcript가 아니라 `<session>/subagents/*.jsonl`에 있다. main만 읽으면 에이전트 팀이나 서브에이전트를 많이 쓰는 작업일수록 적게 잡힌다. `promptId`로 해당 턴에 귀속할 수 있다(66개 중 65개 일치).
- **Codex 하위 스레드:** `root_turn_id`로 루트 턴에 귀속한다. 실측에서 레코드의 절반 가까이가 여기에 해당했다.
- **재개한 세션:** 재개할 때 이전 항목이 새 파일에 다시 기록될 수 있어, 파일 사이에서도 중복을 제거해야 한다(`session-report` 분석기는 항목 `uuid`로 제거한다).
- **부가 호출:** 제목 생성 같은 haiku 호출과 압축(compact) 요약 호출은 transcript에 usage가 없다. `compact_boundary` 항목에는 압축 전후 토큰 수만 있다.

**권고:** 문서에 "측정 범위 = transcript에 기록된 호출(서브에이전트 포함)"이라고 명시한다. 세션 단위로 `cost-state`나 OTel 합계와 대조해 **커버리지(%)를 함께 표시**하면 결과를 어디까지 믿을 수 있는지 사용자가 알 수 있다.

### 3. Normalized Usage 정의에 이중 계산 함정이 있다

같은 이름의 필드라도 Provider마다 뜻이 다르다. 원문 7.5절 표를 다음처럼 정의하길 권한다.

| 정규화 필드 | Claude Code | Codex |
|---|---|---|
| `uncachedInput` | `input_tokens` | `input_tokens − cached_input_tokens` |
| `cacheRead` | `cache_read_input_tokens` | `cached_input_tokens` |
| `cacheWrite5m` / `cacheWrite1h` | `cache_creation.ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens` | `cache_write_input_tokens`(현재 0, TTL 구분 없음) |
| `output`(reasoning 포함) | `output_tokens` | `output_tokens` |
| `reasoning`(output의 일부) | 별도 필드 없음(`output_tokens_details` 내용 확인 필요) | `reasoning_output_tokens` |

- Codex의 `input_tokens`를 Claude와 같은 "Input"으로 더하면 캐시분이 두 번 들어간다.
- `output`과 `reasoning`을 더하면 reasoning이 두 번 들어간다. reasoning은 합계가 아니라 내역으로만 보여준다.
- Claude 캐시 쓰기는 5분 TTL이 기본 입력 단가의 1.25배, 1시간 TTL이 2배다. 하나로 합치면 비용을 추정할 수 없다.
- 중복 제거 규칙도 명시한다. 키는 `requestId`(없으면 `message.id`)이고, 값은 **마지막 줄(output 최댓값)**을 쓴다.

### 4. "Processed" 합계는 비교 지표가 될 수 없다

원문 예시의 Processed 906,184 중 cache read가 811,291(89.5%)이다. Anthropic 기준 캐시 읽기는 기본 입력 단가의 0.1배다. 입력 쪽을 단가 기준으로 환산하면 다음과 같다.

| 항목 | 토큰 | 가중치 | 입력 환산 |
|---|---:|---:|---:|
| Fresh Input | 3,842 | 1 | 3,842 |
| Cache Read | 811,291 | 0.1 | 81,129 |
| Cache Write(5분 TTL 가정) | 71,230 | 1.25 | 89,038 |
| **합계** | **886,363** | | **약 174K**(1시간 TTL이면 약 227K) |

캐시 적중률을 높인 하네스는 Processed가 그대로이거나 늘어도 실제 비용은 크게 줄 수 있다. Processed로 비교하면 결론이 뒤집힐 수 있다.

**권고:** 기본 출력에 다음을 넣는다. 모두 로컬 계산이라 LLM이 필요 없다.

- 단가표 파일 기반의 **비용 또는 입력 환산값**. 원문에서 Later인 "비용 환산"을 MVP로 당긴다.
- **턴 내 최대 컨텍스트 크기**: 가장 큰 호출의 `uncachedInput + cacheRead + cacheWrite`
- **API 호출 수**

### 5. 비교가 목표라면 구성 정보를 MVP부터 남긴다

장기 목표인 "모델·Harness·Skill 구조 변경 전후 비교"는 **턴을 기록할 때의 구성 정보**가 있어야 가능하다. Claude Code transcript는 기본 30일(`cleanupPeriodDays`)이 지나면 지워지므로 나중에는 복원할 수 없다.

- **로그에서 바로 얻는 값:** 모델, `effort`, `gitBranch`, `cwd`, 클라이언트 버전(Claude). 호출 단위 `attributionSkill`·`attributionPlugin`·`attributionMcpServer`·`attributionMcpTool`(Claude). `turn_context`(Codex)
- **Hook이 수집하면 좋은 값:** `CLAUDE.md`/`AGENTS.md` 해시, git HEAD, 활성 플러그인 목록 해시, 실험 라벨 환경변수(예: `TOKEN_METER_LABEL`)

같은 작업도 실행할 때마다 토큰 편차가 크다. 전후 비교는 반복 실행을 전제로 하므로, 라벨로 실험 조건을 묶을 수 있어야 한다.

### 6. 기존 구현을 먼저 쓰고, 검증 기준으로 삼는다

| 도구 | 이미 하는 것 | 이 프로젝트에서의 쓰임 |
|---|---|---|
| Anthropic 공식 `session-report` 플러그인(`analyze-sessions.mjs`) | `requestId` 중복 제거(최대 output 유지), 서브에이전트 transcript와 `meta.json`, 재개 세션 `uuid` 중복 제거, 서브에이전트와 task-notification 후속 호출을 포함한 **프롬프트별 상위 목록** | Claude 파서의 참조 구현. Prototype은 이 `--json` 출력에서 시작해도 된다 |
| [ccusage](https://github.com/ryoppippi/ccusage) | 일·월·세션·5시간 블록 리포트, Claude Code와 Codex를 포함한 다수 에이전트 지원, `--json` 출력. 턴 단위 분석은 README에 없다 | 세션 합계를 대조하는 검증 기준 |
| Claude Code OpenTelemetry | `prompt.id`로 한 프롬프트의 모든 이벤트를 묶는다. `api_request` 이벤트에 토큰·비용·모델·`query_source`(main/subagent/auxiliary)가 있다 | 부가 호출까지 포함한 2차 소스. 문서화된 인터페이스라 JSONL보다 변경 위험이 낮다. 대신 수집기가 필요하고 과거 소급은 안 된다 |

파서는 이 프로젝트의 차별점이 아니다. 차별점은 **턴·작업 단위 귀속, 구성 정보, 워크스페이스 Ledger**다. 미결정 사항에 "데이터 소스: transcript 우선, OTel 보조"를 추가하길 권한다.

### 7. Hook 운용 세부

- Claude Code에서 exit code 2는 차단 신호다. `UserPromptSubmit`에서 2로 끝나면 프롬프트가 막힌다. 모든 경로가 exit 0으로 끝나도록 최상위에서 예외를 처리한다.
- 동기 Hook은 프롬프트 제출과 응답 종료를 지연시킨다(`UserPromptSubmit` 기본 타임아웃은 30초). 1번 권고대로 `Stop`에만 `async: true`로 건다.
- 여러 세션이 하나의 `ledger.jsonl`에 동시에 append하면 Windows에서 파일 잠금 충돌이나 줄 섞임이 생길 수 있다. 세션별 파일로 나누거나, 턴 키로 upsert할 수 있는 SQLite를 쓴다.

### 8. 성능 최적화는 측정한 뒤에

원문 9절(기술적 고려사항)은 Offset·Checkpoint·증분 파싱을 검토 대상으로 둔다. 실측으로는 가장 큰 transcript도 전체 파싱이 약 0.16초이고, Stop Hook을 async로 두면 사용자 대기 시간에 들어가지 않는다. MVP는 세션 전체를 다시 파싱하고, 증분 파싱은 측정해서 문제가 확인되면 넣는다.

### 9. 기타

- **저장 위치:** `<workspace>/.token-meter/`는 git 작업 트리에 추적되지 않는 파일을 만들고, worktree나 서브모듈 허브에서는 데이터가 흩어진다. 사용자 전역 저장소를 기본으로 두고 workspace(git root·remote)를 조회 조건으로 쓰며, 필요할 때 workspace별로 내보내는 편이 낫다.
- **Task 경계 후보에 git 브랜치 추가:** 작업마다 feature 브랜치를 쓰는 규칙이라면 `gitBranch`는 LLM 없이 얻는 Task 키다. transcript의 모든 항목에 들어 있다. 기본값은 브랜치로 두고 `task start/end`로 덮어쓰는 방식을 제안한다.
- **구현 언어:** 위 측정대로 시작 시간은 결정 요인이 아니다. Prototype은 참조 구현을 옮기기 쉬운 Python으로, MVP는 계속 유지보수할 언어(예: 단일 파일로 배포하는 C#)로 정하면 된다.
- **Ledger 예시의 `inputTokens`:** Claude 기준인지 Codex 기준인지 모호하다. 3번 정의를 따른다.

## 제안하는 진행 순서

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| Prototype (Hook 없음) | `token-meter last`, `token-meter turns <session>`: main과 서브에이전트 transcript, Codex rollout을 읽어 턴 ID별로 집계한다. 3번 정규화와 단가표를 적용한다 | 세션 합계가 `session-report`·ccusage와 일치한다. `cost-state` 대비 커버리지를 표시한다 |
| MVP | `Stop`(async) Hook → 세션 재집계 → 전역 Ledger에 턴 키로 upsert. 5번 구성 정보를 기록한다 | 중단된 턴, 백그라운드 서브에이전트, 재개한 세션을 재현한 테스트를 통과한다 |
| Phase 2 | Task: 브랜치를 기본 키로 쓰고 `task start/end`로 덮어쓴다. `history`, `stats`, 라벨별 비교 | 같은 작업을 반복 실행해 조건별 편차를 보고할 수 있다 |

## 확인한 사실과 측정이 필요한 추정

**직접 확인한 것**

- 로그 구조와 수치: 2026-09-19 이 PC의 로그를 분석했다(Claude Code 2.1.277, Codex 0.153.4). 중복 제거 통계는 크기 상위 40개 transcript와 서브에이전트 transcript 40개, `promptId` 통계는 transcript 275개 전체, Codex 통계는 최근 rollout 30~60개 기준이다.
- Claude Code Hook: 공통 입력의 `prompt_id`, `async` 옵션, exit 2만 차단, `UserPromptSubmit` 기본 타임아웃 30초, `Stop`/`SubagentStop`의 `last_assistant_message` — [Hooks 문서](https://code.claude.com/docs/en/hooks)
- Codex Hook: 기본 활성화, Windows 지원, `Stop`·`UserPromptSubmit` 등의 `turn_id`, 중단 시 Stop 대신 Interrupt 이벤트 — [Codex Hooks 문서](https://learn.chatgpt.com/docs/hooks), [openai/codex#22858](https://github.com/openai/codex/issues/22858)
- Claude Code OTel: `prompt.id`, `claude_code.token.usage`의 `query_source`(main/subagent/auxiliary) 속성 — [Monitoring 문서](https://code.claude.com/docs/en/monitoring-usage)
- `session-report` 분석기의 중복 제거·서브에이전트·재개 세션 처리 — 로컬에 설치된 `claude-plugins-official/session-report`의 `analyze-sessions.mjs` 주석
- ccusage 기능 범위 — [ryoppippi/ccusage](https://github.com/ryoppippi/ccusage) README

**측정이 필요한 추정**

- Hook 입력의 `prompt_id`가 transcript의 `promptId`와 같은 값인지
- Claude Code에서 Esc로 중단했을 때 `Stop`이 발생하는지
- OTel `api_request` 이벤트의 실제 필드와 부가 호출 포함 여부(문서 요약 기준으로만 확인)
- 압축(compact) 요약 호출의 usage를 얻을 방법
- Claude `output_tokens_details`의 내용(thinking 토큰 포함 여부)
- `cost-state`가 기록되는 시점. 커버리지 비교 오차의 원인일 수 있다
- Codex `cache_write_input_tokens`가 0이 아닌 경우가 있는지

## 기존 문서와의 관계

- [Task Token Meter](./task-token-meter.md) — 검토 대상 원문
- [Code-Virtualize](https://github.com/jaywapp/wiki/blob/develop/ai/harness/code-virtualize.md) — 원문이 측정 대상으로 꼽은 `.cv`. [Code-Virtualize 검토](https://github.com/jaywapp/wiki/blob/develop/ai/harness/code-virtualize-feedback-claude.md)가 권고한 Phase 0 측정(Read/Grep 결과 비중, 전체 파일 읽기와 재읽기 비율)을 이 도구의 턴 단위 집계로 할 수 있다
- [Claude + Codex 토큰 최적화 워크플로우 설계](https://github.com/jaywapp/wiki/blob/develop/ai/tips/token-optimization-claude-codex.md) — 이 도구로 효과를 측정할 최적화 기법들
