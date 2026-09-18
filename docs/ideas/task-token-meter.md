- Status: Idea
- Version: 0.1
- Created: 2026-09-19
- Updated: 2026-09-19
- Tags: ai, claude-code, codex, token, harness, developer-tools, observability

# Task Token Meter

## 1. 한 줄 요약

Claude Code, Codex 등 AI 코딩 에이전트를 특정 워크스페이스에서 사용할 때, 세션 전체가 아닌 **개별 작업(Task/Turn) 단위의 실제 토큰 사용량을 자동으로 측정·기록하는 로컬 도구**를 만든다.

토큰 계산은 AI나 Skill이 아니라 Hook과 외부 실행 프로그램이 담당해, 토큰 사용량을 측정하느라 추가 LLM 토큰을 소비하는 구조를 최소화한다.

## 2. 배경 및 문제

Claude Code나 Codex로 개발할 때는 하나의 세션을 오래 유지하면서 여러 작업을 연속으로 수행하는 경우가 많다.

```text
1. A 기능 구현
2. A 작업 완료
3. B 버그 수정
4. B 작업 완료
```

이때 세션 전체 토큰 사용량만 보면 다음 결과를 얻는다.

| 시점 | 세션 누적 |
|---|---:|
| A 종료 | 30K |
| B 종료 | 50K |

하지만 실제로 알고 싶은 정보는 작업별 소비량이다.

| 작업 | 소비량 |
|---|---:|
| A | 30K |
| B | 20K |

즉 필요한 것은 단순한 Session Token Counter가 아니라 **작업 경계를 기준으로 토큰 소비량을 측정하는 Task Token Meter**다.

이 정보는 비용 확인뿐 아니라 AI 개발 환경 최적화에도 쓸 수 있다.

- 동일한 버그 수정 작업의 모델별 토큰 사용량
- Harness 적용 전/후 토큰 사용량
- Context 최적화 적용 전/후 토큰 사용량
- 특정 프로젝트의 작업 유형별 평균 토큰 소비량
- `.cv`, Virtual Remark 등 컨텍스트 최적화 도구의 적용 효과

### 기존 방식의 문제

AI Skill이 로그를 읽고 토큰을 계산하게 만들 수도 있지만, 그러면 토큰 사용량을 측정하려고 다시 LLM을 호출하게 된다. 이는 이 도구의 목적과 충돌한다.

따라서 토큰 수집·계산·저장은 exe, py, ps1 등 **로컬 외부 프로그램(CLI)** 이 담당한다. Skill이 필요하더라도 계산 로직 없이 외부 도구를 호출하는 최소 인터페이스 역할만 맡는다.

## 3. 목표

### 핵심 목표

사용자가 별도로 토큰을 계산하지 않아도 Claude Code/Codex에서 수행한 **개별 작업의 토큰 사용량을 확인**할 수 있게 한다.

### 기대 결과

```text
Task: Submit Dialog 개선
Provider: Claude Code
Turns: 5
Duration: 18m 42s

Fresh Input       3,842
Cache Read      811,291
Cache Write      71,230
Output           19,821
────────────────────────
Processed       906,184
```

### 장기 목표

축적된 데이터로 다음을 분석할 수 있는 기반을 만든다.

- 작업 유형별 토큰 비용
- 모델별 효율
- 프로젝트별 AI 사용량
- Harness별 효율
- Context 최적화 효과
- Skill/Agent 구조 변경 전후 비교

장기적으로는 단순 Token Counter를 넘어 **AI 개발 워크플로우의 비용과 Context 효율을 측정하는 Observability 계층**으로 발전할 수 있다.

## 4. 대상 사용자

### 주요 사용자

Claude Code 또는 Codex로 하나의 세션에서 여러 개발 작업을 수행하는 개발자. 특히 다음 환경을 주요 대상으로 한다.

- 장시간 유지되는 AI Coding Session
- 프로젝트별 Workspace
- Claude Code, Codex
- AI Harness 기반 개발 환경

### 핵심 요구

AI 작업 과정에 추가 절차를 최대한 넣지 않고 다음을 알고 싶다.

> "방금 끝난 작업에 토큰을 얼마나 사용했는가?"

## 5. 핵심 아이디어

AI가 토큰을 계산하지 않는다. Claude Code와 Codex가 생성하는 세션 정보, Hook 이벤트, Transcript/JSONL, Usage 데이터를 외부 프로그램이 읽어 계산한다.

```text
Claude Code / Codex
        │
        │ Hook
        ▼
  Task Token Meter
        │
   Provider Adapter
    ┌───┴────┐
    │        │
  Claude    Codex
  Parser    Parser
    │        │
    └───┬────┘
        │
   Usage Normalize
        │
        ▼
  Workspace Ledger
```

### 측정 계층

측정 단위는 장기적으로 다음 구조를 사용한다.

```text
Session
 │
 ├── Task A
 │    ├── Turn 1
 │    ├── Turn 2
 │    └── Turn 3
 │
 └── Task B
      ├── Turn 4
      └── Turn 5
```

| 단위 | 정의 | 예 |
|---|---|---|
| Session | 하나의 Claude Code/Codex 세션 | — |
| Task | 사용자 관점에서 하나의 목적을 가진 작업. 여러 Turn으로 구성될 수 있다 | Submit Dialog 개선, HashTagControl 버그 수정, Sitemap 리팩터링 |
| Turn | 사용자 Prompt 1회와 그에 따른 AI 작업의 실행 단위 | — |

## 6. 주요 사용 흐름

### 기본 자동 측정

사용자는 별도의 측정 명령을 실행하지 않는다.

```text
사용자 Prompt
      │
      ▼
UserPromptSubmit ──▶ Turn Start
      │
      ▼
   AI 작업
      │
      ▼
    Stop ──▶ Turn End
      │
      ▼
  Usage 계산
      │
      ▼
  Ledger 저장
```

### 결과 확인 (CLI)

```text
token-meter current
tm current          # 짧은 alias를 제공할 경우
```

```text
Task: Submit Dialog 개선
Provider: Claude Code
Session: 74f9...
Turns: 5
Duration: 18m 42s

Fresh Input       3,842
Cache Read      811,291
Cache Write      71,230
Output           19,821
────────────────────────
Processed       906,184
```

### Skill을 통한 조회 (선택)

필요하면 `/token-summarize` 같은 Skill을 제공할 수 있다. 단 Skill은 로그를 읽거나 계산하지 않고 내부적으로 `token-meter current`만 실행한다. Skill의 역할은 CLI 호출 인터페이스로 제한한다.

## 7. 핵심 기능

### 7.1 Turn 자동 측정

**우선순위**: MVP

사용자 Prompt 제출부터 AI 응답·작업 종료까지 발생한 토큰을 하나의 Turn으로 기록한다. 가능한 경우 Hook으로 경계를 자동 감지한다.

**필요한 이유**: 사용자가 측정 시작/종료 명령을 입력하지 않아도 가장 기본적인 작업별 토큰 사용량을 확인할 수 있다.

### 7.2 Provider Adapter

**우선순위**: MVP

Claude Code와 Codex는 로그·Usage 구조가 다르므로 Provider Adapter를 분리하고, Provider별 원본 데이터를 공통 Usage 모델로 변환한다.

```text
adapters/
  claude/
  codex/
```

### 7.3 Claude Usage Parser

**우선순위**: MVP

Claude Code 세션 JSONL에서 실제 usage를 추출한다.

- `input_tokens`
- `cache_creation_input_tokens`
- `cache_read_input_tokens`
- `output_tokens`

Claude Code JSONL에는 동일 API response의 usage가 여러 entry에 나타날 수 있으므로 **단순 SUM을 쓰지 않는다.** message ID 등 식별 정보로 중복 usage를 제거한 뒤 집계한다.

### 7.4 Codex Usage Parser

**우선순위**: MVP

Codex가 제공하는 Turn/Session usage를 수집한다. 가능하면 Codex가 기록한 실제 usage를 우선 사용한다.

- `input_tokens`
- `cached_input_tokens`
- `output_tokens`
- `reasoning_output_tokens`

### 7.5 Usage Normalization

**우선순위**: MVP

Provider마다 다른 Usage 구조를 억지로 같게 만들지 않고, 다음 두 종류를 함께 유지한다.

1. Provider Native Usage
2. Normalized Usage

| Metric | Claude | Codex |
|---|---|---|
| Input | 지원 | 지원 |
| Output | 지원 | 지원 |
| Cached Read | 지원 | 지원 |
| Cached Write | 지원 | 해당 없음 또는 별도 처리 |
| Reasoning Output | 해당 없음 또는 별도 처리 | 지원 |

Provider 고유 데이터가 손실되지 않아야 한다.

### 7.6 Workspace Ledger

**우선순위**: MVP

측정 결과를 Workspace 내부 또는 사용자 설정 경로에 저장한다.

```text
.token-meter/
  ledger.jsonl
```

예시 레코드 (정확한 Schema는 구현 설계 단계에서 확정):

```json
{
  "session": "01J...",
  "workspace": "P4VCustom",
  "provider": "claude-code",
  "turn": "...",
  "startedAt": "...",
  "endedAt": "...",
  "usage": {
    "inputTokens": 18320,
    "outputTokens": 4210,
    "cacheReadTokens": 44100,
    "cacheWriteTokens": 5200
  }
}
```

### 7.7 Task Grouping

**우선순위**: Later

하나의 실제 작업이 여러 Turn에 걸칠 수 있다.

```text
사용자: "A 기능 구현해줘."
AI:     "A 방식과 B 방식 중 어떤 것을 사용할까요?"
사용자: "A로 해줘."
```

시스템상 두 Turn이지만 사용자 관점에서는 하나의 Task다. 장기적으로 다음 구조를 지원한다.

```text
Task A
├── Turn 18
├── Turn 19
├── Turn 20
└── Turn 21
```

초기에는 의미 기반 자동 Task 판단을 하지 않고, 필요하면 명시적으로 관리한다.

```text
token-meter task start "Submit Dialog 개선"
token-meter task end
```

### 7.8 History / Statistics

**우선순위**: Later

축적된 Ledger로 `token-meter history`, `token-meter stats`를 제공한다.

```text
P4VCustom / This Week

Submit Dialog       22.5K
HashTagControl      11.6K
Sitemap             48.1K
Refactoring        121.3K
─────────────────────────
Total              203.5K
```

이후 작업 유형, 모델, Harness 등의 조건으로 비교할 수 있게 확장한다.

## 8. 범위

### MVP

MVP의 핵심은 **Turn Meter**다. 기본 정의는 다음과 같다.

> 사용자 Prompt 1회와 그 Prompt로 실행된 AI 작업을 하나의 Turn으로 측정한다.

- Claude Code 지원
- Codex 지원
- Provider Adapter 구조
- Hook 기반 Turn 시작/종료 감지
- 사용자 Prompt 단위 자동 측정
- Provider 실제 Usage 데이터 수집
- Claude JSONL Usage 중복 제거
- Provider Native Usage 보존
- Normalized Usage 생성
- Workspace Ledger 기록
- 현재/직전 Turn Usage CLI 조회

### Later

- 여러 Turn을 하나의 Task로 Grouping, `task start/end`
- Task·Session·Workspace별 통계
- 프로젝트·모델·Harness별 비교
- 기간별 Token Trend, 작업 유형별 평균 Token
- 비용 환산
- Dashboard
- `/token-summarize` Skill
- 자동 Task Boundary 추론
- Harness Benchmark 기능

### Out of Scope

- LLM을 이용한 토큰 계산
- LLM을 이용한 로그 분석
- LLM을 이용한 자동 Task 분류
- 복잡한 Web Dashboard
- 조직 단위 사용량 관리
- Billing 시스템 대체
- Claude/OpenAI 공식 비용 청구 데이터 대체

## 9. 기술적 고려사항

### Claude Code

세션 JSONL의 Usage 정보를 사용한다. 모든 Assistant Message의 Usage를 단순 합산하면 중복 집계될 수 있으므로 Usage Entry 식별과 Deduplication이 필요하다.

### Codex

Turn 단위 Usage와 Session/Transcript 정보를 활용한다. 가능하면 Provider가 직접 제공하는 Turn Usage를 우선한다.

### Hook 실패

Token Meter 장애가 Claude Code/Codex의 실제 개발 작업을 멈추게 해서는 안 된다. Hook 실행 실패 시 기본 정책은 다음과 같다.

```text
Token Meter 실패 → 오류 로깅 → AI 작업은 계속 진행
```

Token Meter는 개발 Workflow의 보조 Observability 도구다.

### 성능

Hook에서 실행되는 작업은 최대한 가벼워야 한다. 매번 전체 Session JSONL을 처음부터 재분석하지 않도록 다음 방식을 검토한다.

- Offset
- Checkpoint
- Message ID
- Turn ID
- Incremental Parsing

## 10. 위험 요소 및 대응

| 위험 | 내용 | 대응 |
|---|---|---|
| Provider 로그 구조 변경 | Claude Code/Codex 업데이트로 JSONL 또는 Usage Schema가 바뀔 수 있다 | Provider Adapter를 분리해 변경 영향을 격리한다 |
| Usage 중복 집계 | 특히 Claude Code JSONL에서 동일 Usage가 여러 Entry에 기록될 수 있다 | Message/Request Identity 기반 Deduplication을 구현하고 실제 세션으로 검증 테스트를 만든다 |
| Task 경계 모호성 | 사용자 Prompt 하나가 항상 실제 Task 하나를 뜻하지 않는다 | MVP는 Turn 측정까지만 자동화하고 Task Grouping은 별도 계층으로 분리한다 |
| 측정 도구 자체의 복잡성 | Dashboard·비용 분석·Task 자동 분류까지 처음부터 넣으면 핵심 가치 검증이 늦어진다 | 첫 버전은 Hook → Usage → Ledger → CLI 흐름만 구현한다 |

## 11. 단계별 구현

### Prototype

실제 Claude Code와 Codex 세션 데이터를 수집해 다음을 검증한다.

- Hook에서 얻을 수 있는 정보
- Session 식별 가능 여부
- Turn 식별 가능 여부
- Usage 추출 정확도
- Claude JSONL Deduplication
- Codex Usage Mapping

목표:

> "사용자 Prompt 하나가 끝난 직후 해당 Turn이 소비한 실제 Token을 정확하게 출력할 수 있는가?"

### MVP

다음 흐름을 완성한다.

```text
Claude / Codex → Hook → Turn Meter → Usage Parser → Ledger → CLI
```

### Phase 2

Task 개념을 추가한다.

```text
Session → Task → Turn
```

`task start/end`와 여러 Turn의 Usage Aggregation을 지원한다.

## 12. 결정된 사항

- **외부 프로그램 중심**: 토큰 측정·계산은 AI가 아니라 로컬 외부 프로그램(exe, py, ps1, CLI 중 선택)이 수행한다. 최종 배포 방식은 기술 설계 단계에서 결정한다.
- **Skill 의존 최소화**: Skill이 필요해도 계산 로직을 넣지 않고 외부 도구 호출 인터페이스 역할만 맡긴다.
- **Claude Code + Codex 지원**: 특정 Provider 전용이 아니라 두 Provider를 모두 지원하는 Adapter 구조를 쓴다.
- **실제 Usage 우선**: tokenizer로 재계산하기보다 Provider가 기록한 실제 Usage를 우선 사용한다.
- **작업 단위 측정**: 핵심 목적은 Session Total 조회가 아니라 개별 작업의 Token Consumption 파악이다.
- **원본 Usage 보존**: Provider마다 다른 Token Metric을 하나의 숫자로 강제 변환하지 않고, Native Usage를 보존하면서 Normalized Usage를 별도로 제공한다.

## 13. 미결정 사항

### Task Boundary 자동화 방식

MVP 이후 여러 Turn을 하나의 Task로 묶을 때 경계를 어떻게 정할지 검토가 필요하다.

1. 사용자의 `task start/end`
2. CLI 기반 Checkpoint
3. Skill을 통한 경계 지정
4. Hook 및 규칙 기반 추론
5. 선택적인 AI 기반 Task 분류

AI 기반 방식은 추가 Token Consumption이 생기므로 기본 방식으로 쓰지 않는 방향을 우선 검토한다.

### 저장 위치

- 후보: `<workspace>/.token-meter/` 또는 사용자 전역 저장소
- Workspace별 분석이 목적이므로 Workspace 로컬 저장을 기본으로 하고, 전역 Aggregation을 별도로 제공하는 구조를 검토한다.

### 구현 언어 및 배포 형태

- 후보: Python, PowerShell, .NET executable, 기타 단일 실행 CLI
- 판단 기준: Windows 설치 편의성, Claude Code/Codex Hook 연동, JSONL 처리 성능, 단일 실행 파일 배포, 향후 Cross-platform 지원

## 14. 다음 작업

1. Prototype 검증 항목에 따라 실제 Claude Code·Codex 세션 데이터를 수집하고 Hook·Usage 구조를 확인한다.
2. 구현 언어·배포 형태와 Ledger 저장 위치를 결정한다.
3. Ledger Schema(Native/Normalized Usage)를 확정한다.
4. Hook → Usage → Ledger → CLI 흐름의 MVP를 구현한다.
