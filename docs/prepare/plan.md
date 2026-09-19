# Plan — 구현 작업 계획

작성일: 2026-09-19 · 상태: 조건부 계획, 제품 구현 미착수

## 계획의 기준

[design.md](design.md), [architecture.md](architecture.md), [user-confirm.md](user-confirm.md)를 작성한 뒤 이 계획을 마지막으로 작성했다. 기준 아이디어는 `docs/ideas/`의 두 문서 전체이며 기준 커밋은 `148f7e7d4919f90df1c37dbc1036622e337f5eda`다.

- 문서 준비는 완료할 수 있지만, Pending 결정을 무시하고 제품 설계를 확정하지 않는다.
- `Blocked By`는 직접 차단이며 Dependencies를 통해 후속 작업에도 전파된다.
- 구현 경로는 C# 추천안을 선택한 경우의 예상 경로다. UC-002가 바뀌면 TASK-002에서 먼저 재작성한다.
- TASK-001과 TASK-004의 합성 fixture/계약 조사는 제품 언어·저장소 결정 전 진행 가능하다. 실제 개인 로그는 별도 동의를 확인한 범위에서 구조/수치만 조사하고 합성 fixture를 기본으로 한다.
- 구현·검증 기록에 소요 시간, 모델, 관련 fixture, 실제 Provider 버전을 남긴다.

## UI 필요 여부와 시안 생략

MVP의 사용자 인터페이스는 터미널 CLI다. TUI·웹·데스크톱 그래픽 화면은 없고 원안은 Dashboard를 Later로 분류한다. 따라서 그래픽 UI 비교용 `samples/sample1~3`을 만들지 않는다. 조회 완료/진행 중/세션 모호성의 CLI 출력 예시는 design.md에 있고, JSON·무색 출력·80열·리디렉션을 TASK-010에서 검증한다.

`taste`/`impeccable`에 따른 화면 시안 생성은 그래픽 UI가 범위에 들어올 때 수행한다. 이번에는 관련 스킬을 참고했지만 웹 UI 생성 절차는 적용하지 않았다. 장식용 웹 시안을 만들기 위해 제품 범위를 늘리지 않는다.

## 작업 모델 선택

현재 실행 환경에 명시적으로 제공된 Codex 모델 중 `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`를 작업별로 배정한다. Claude 상위 모델을 설계·교차 리뷰에 쓰는 로컬 선호는 있지만 이 세션에는 호출 가능한 Claude 모델이 노출되지 않았으므로 가상의 Claude 버전이나 실행을 약속하지 않는다. 실행 시 Claude가 사용 가능하면 모델 가용성을 확인하고 설계/리뷰 작업의 배정을 갱신할 수 있다.

여기의 모델 지정은 후속 실행을 위한 계획이며, 이번 준비 작업에 해당 모델의 서브에이전트를 실행했다는 뜻이 아니다. 추론 수준은 이 스킬의 `Low / Medium / High / Extra High` 표기를 쓴다.

## 실행 순서와 병렬화

```mermaid
flowchart TD
    T01[TASK-001 Provider 계약 조사] --> T04[TASK-004 Golden fixtures]
    T01 --> T02[TASK-002 결정 반영]
    UC[UC-001~008 사용자 결정] --> T02
    T02 --> T03[TASK-003 프로젝트 기반]
    T03 --> T05[TASK-005 Claude Adapter]
    T03 --> T06[TASK-006 Codex Adapter]
    T03 --> T08[TASK-008 정규화]
    T04 --> T05
    T04 --> T06
    T04 --> T08
    T05 --> T07[TASK-007 root 귀속]
    T06 --> T07
    T07 --> T09[TASK-009 Ledger]
    T08 --> T09
    T09 --> T10[TASK-010 CLI]
    T10 --> T11[TASK-011 Hook]
    T10 --> T12[TASK-012 선택적 비용]
    T11 --> T13[TASK-013 E2E 및 배포 검증]
    T12 -. UC-006에서 MVP 선택 시 .-> T13
    T13 --> T14[TASK-014 교차 리뷰와 인수 문서]
```

003 이후 005/006/008은 Core 계약이 고정됐을 때 서로 다른 파일을 맡겨 병렬화할 수 있다. 011/012도 독립 경로로 분리 가능하다. Core 계약·schema·동일 브랜치의 Git 상태를 동시에 수정하지 않는다. 실제 위임 여부는 당시 도구·모델 가용성과 토큰 비용을 보고 결정한다.

## TASK-001 — Provider 계약과 관측 한계 조사

### Goal
아이디어의 실측 보고를 구현 가능한 버전별 계약과 반례 목록으로 바꾼다.
### Dependencies
없음. docs/ideas 두 문서 전체와 architecture의 불확실성 목록을 읽는다.
### Blocked By
없음. 개인 로그 접근이 미승인인 경우 공개 공식 문서와 합성 자료로 먼저 진행하고 실측 항목은 미검증으로 남긴다.
### Scope
- 현재 공식 Hook 문서와 설치 대상 버전 대조. prompt_id/promptId, turn_id/root_turn_id, root session lineage 확인.
- Claude request/message alias, snapshot 최댓값·모순 사례, 재개/fork·task-notification 귀속 규칙 확인.
- Codex call delta/turn snapshot/session snapshot 분류와 root main-only/child-inclusive 구분.
- nonzero cache write, total-only, 누락 ID, Esc·늦은 child·Hook stdout 계약 검증 방법 정리.
- session-report/ccusage 버전·라이선스·scope 차이를 기록. API 호출 없이 수행 가능한 증거를 우선한다.
### Files
`docs/research/provider-contracts.md`, `docs/research/source-matrix.md`.
### Validation
각 주장에 Provider 버전·근거·확인/미확인 상태와 재현 절차가 있다. ID와 snapshot 의미가 확인되지 않은 형식은 unsupported로 분류한다. 민감 원문이 산출물에 없다.
### Agent
Codex
### Model
gpt-6-astra
### Reasoning Level
High
### Reason
서로 다른 계측 의미와 예외를 판단하는 작업이므로 상위 모델을 사용한다. 대규모 로그 전체를 모델 context에 넣지 않는다.

## TASK-002 — 사용자 결정 반영과 계약 고정

### Goal
선택된 범위에 맞게 세 준비 문서와 본 계획을 일관되게 고정한다.
### Dependencies
TASK-001.
### Blocked By
UC-001, UC-002, UC-003, UC-004, UC-005, UC-006, UC-007, UC-008.
### Scope
- 사용자 선택과 날짜·근거를 기록하고 해당 항목을 Confirmed로 변경한다.
- 다른 저장소/언어/Hook 옵션이면 architecture·예상 Files·인수 조건을 재작성한다.
- UC-006=A이면 TASK-012를 Deferred로, B이면 필수로 전환한다. C이면 환산식·명칭·검증을 별도 설계한다.
- Provider별 지원 capability, neutral Hook response, 정규화 계약을 고정한다.
### Files
`docs/prepare/design.md`, `architecture.md`, `user-confirm.md`, `plan.md`.
### Validation
Pending인 채 시작되는 제품 작업이 없고, 변경 옵션과 모순되는 예시·경로·수용 기준이 없다. 기술 미확인 항목이 구현 계약으로 승격되지 않았다.
### Agent
Codex
### Model
gpt-6-astra
### Reasoning Level
High
### Reason
여러 문서와 사용자 결정을 통합해야 하며 잘못된 범위 확정의 비용이 크다.

## TASK-003 — 빌드 가능한 CLI 프로젝트와 계약 타입

### Goal
선택된 runtime에서 Core/Adapter/Storage/CLI를 독립 검증할 기반을 만든다.
### Dependencies
TASK-002.
### Blocked By
UC-001, UC-002; 나머지는 TASK-002를 통해 전파.
### Scope
정식 solution/project, SDK·패키지 lock, nullable·checked token 연산, interface/DTO, 테스트 프로젝트, 최소 CI build/test를 만든다. 실제 provider parser나 임시 전역 Hook을 넣지 않는다.
### Files
`TaskTokenMeter.sln`, `global.json`, `Directory.Build.props`, `src/*/*.csproj`, `src/TaskTokenMeter.Core/Contracts/`, `tests/*/*.csproj`, `.github/workflows/ci.yml`.
### Validation
clean checkout에서 restore/build/test 명령이 성공한다. Core가 CLI·Provider 설치에 의존하지 않는다. JSON에 schemaVersion 및 nullable 수치가 유지된다.
### Agent
Codex
### Model
gpt-5.6-luna
### Reasoning Level
Medium
### Reason
고정된 계약의 프로젝트·타입 생성 중심이라 저비용 모델로 충분하다.

## TASK-004 — 합성 Golden fixture와 oracle 정의

### Goal
실제 개인정보 없이 정답이 계산된 Provider별·공통 반례 묶음을 만든다.
### Dependencies
TASK-001.
### Blocked By
없음. 미확정 scope는 서로 다른 대안 fixture로 명시하고 제품 기본값으로 정하지 않는다.
### Scope
2턴, streaming duplicate, alias 혼재, total-only, root inclusive/main-only, 늦은 child, fork 복제, partial tail, 손상행, model change, 누락 identity를 최소 JSONL로 작성한다. 기대 native/normalized usage·귀속·quality를 손계산하고 fixture별 출처/합성 여부를 적는다.
### Files
`tests/fixtures/claude/`, `tests/fixtures/codex/`, `tests/fixtures/expected/`, `tests/fixtures/README.md`.
### Validation
모든 fixture에 독립적인 기대값과 목적이 있고 원문 대화/키/실제 경로가 없다. 정상 0·unknown·invalid를 구분하는 반례가 있다. 단순 구현 복사 테스트가 아니다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
정답 fixture가 정확성의 기준이므로 경계 조건과 수치 관계를 충분히 검토한다.

## TASK-005 — Claude 로그 Adapter

### Goal
Claude 지원 버전의 관측을 중복 없는 호출과 턴 후보로 변환한다.
### Dependencies
TASK-003, TASK-004.
### Blocked By
UC-001; TASK-002의 결정 게이트 전파.
### Scope
source snapshot 읽기, UUID 중복 제거, request/message alias와 compatible revision 선택, promptId·parent metadata 추출, TTL breakdown, 지원 형식 판별을 구현한다. 다른 호출의 필드별 max를 합치지 않는다.
### Files
`src/TaskTokenMeter.Adapters/Claude/`, `tests/TaskTokenMeter.UnitTests/ClaudeAdapterTests.cs`.
### Validation
9→9→338은 output 338 한 번이다. 파일 순서를 바꿔도 정답이 같으며 alias 누락·충돌·재개·tail fixture가 통과한다. 원본 본문이 출력/저장 DTO에 없다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
계약은 고정됐지만 identity와 streaming의 예외 구현에 높은 주의가 필요하다.

## TASK-006 — Codex usage Adapter

### Goal
검증된 Codex usage authority와 root 관계를 보존한다.
### Dependencies
TASK-003, TASK-004.
### Blocked By
UC-001; TASK-002의 결정 게이트 전파.
### Scope
지원 rollout/record 버전 판별, usageKind 분류, 누적 snapshot 교체, delta의 검증 전용 사용, lineage 추출, total-only/nonzero cache-write 보존을 구현한다. 구형 session-only 형식을 임의 Turn 차감으로 대체하지 않는다.
### Files
`src/TaskTokenMeter.Adapters/Codex/`, `tests/TaskTokenMeter.UnitTests/CodexAdapterTests.cs`.
### Validation
100→250 snapshot과 대응 delta를 함께 읽어도 250이다. child-inclusive/main-only capability가 구별되고 unknown scope는 partial이다. unsupported 입력이 정상 0건 성공이 되지 않는다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
누적값과 증분값 혼동을 피하면서 검증된 schema를 구현하는 작업이다.

## TASK-007 — 공통 identity, root 귀속, 상태 projection

### Goal
두 Adapter의 실행을 root Turn에 정확히 한 번 귀속한다.
### Dependencies
TASK-005, TASK-006.
### Blocked By
UC-001, UC-005.
### Scope
root session lineage, Membership, 미귀속 observation, source completeness, execution/measurement 상태 분리, 늦은 child 갱신, 재개/fork origin identity를 구현한다.
### Files
`src/TaskTokenMeter.Core/Identity/`, `Attribution/`, `Projection/`, `tests/TaskTokenMeter.UnitTests/AttributionTests.cs`.
### Validation
root 상세와 child 상세를 함께 조회해도 고유 실행 합계가 중복되지 않는다. 부모가 불명확하면 임의 귀속되지 않는다. completed root에 child가 늦게 추가되어도 root key가 유지된다.
### Agent
Codex
### Model
gpt-6-astra
### Reasoning Level
High
### Reason
Provider 차이를 통합하는 핵심 설계 구현이며 오류가 전체 결과를 왜곡한다.

## TASK-008 — 정규화와 품질 지표

### Goal
명시된 포함 관계로 안전하게 토큰 지표를 계산한다.
### Dependencies
TASK-003, TASK-004.
### Blocked By
TASK-002 결정 게이트. 추가 사용자 선택 없음.
### Scope
native 보존, null propagation, knownSubtotal, TTL breakdown 검증, Codex cached/reasoning 부분집합, checked 합계, maxObservedInput·apiCallCount 산출 조건, coverage N/A 조건을 구현한다.
### Files
`src/TaskTokenMeter.Core/Usage/`, `tests/TaskTokenMeter.UnitTests/UsageNormalizationTests.cs`.
### Validation
Codex 1000/600/200/50에서 fresh 400·processed 1200이다. Claude total과 TTL을 중복 합산하지 않는다. 순열·중복 불변성, overflow, null·negative·invalid, coverage 분모 부재/0 테스트 통과.
### Agent
Codex
### Model
gpt-5.6-terra
### Reasoning Level
High
### Reason
정해진 수식 구현이지만 unknown과 포함 관계를 검증하는 테스트가 중요하다.

## TASK-009 — Ledger와 재동기화 내구성

### Goal
동시 수집과 원본 손실에도 기존 정상 결과를 보존한다.
### Dependencies
TASK-003, TASK-004, TASK-007, TASK-008.
### Blocked By
UC-003, UC-004, UC-007.
### Scope
선택된 저장 엔진, unique key·transaction·schemaVersion·migration, expectedRevision/source generation 비교, missing-source 보존, 명시적 purge와 진단 보관 제한을 구현한다. network filesystem 지원을 임의로 넓히지 않는다.
### Files
`src/TaskTokenMeter.Storage/`, `tests/TaskTokenMeter.IntegrationTests/LedgerTests.cs`, `docs/storage.md`.
### Validation
동시 8 writer·같은 snapshot 10회·역순 완료·강제 종료·디스크 쓰기 실패·migration 실패에서 일관성을 유지한다. truncate/권한 거부가 기존 정상값을 0으로 덮어쓰지 않는다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
transaction과 crash 복구는 반복 코드보다 상태 전이 검증의 비중이 크다.

## TASK-010 — CLI 조회와 사용성

### Goal
사용자가 세션·측정 범위·품질을 혼동하지 않고 조회한다.
### Dependencies
TASK-007, TASK-008, TASK-009.
### Blocked By
UC-003, UC-007, UC-008.
### Scope
current/last/turns/sync/rebuild, workspace 탐색, selector, JSON·text renderer, --strict·exit code, fresh/stale 표시, 최소 help와 개인정보 설정을 구현한다. history/stats/task grouping은 제외한다.
### Files
`src/TaskTokenMeter.Cli/`, `tests/TaskTokenMeter.IntegrationTests/CliTests.cs`, `docs/cli.md`.
### Validation
design의 세 출력 시나리오, empty/partial/unsupported/ambiguous 상태가 실행된다. text와 JSON 수치가 같고 JSON stdout은 parse 가능하다. 80열, 한글/공백 경로, NO_COLOR, PowerShell 5.1 리디렉션을 확인한다.
### Agent
Codex
### Model
gpt-5.6-terra
### Reasoning Level
Medium
### Reason
확정된 인터페이스와 정규화 결과를 연결하는 범위가 명확한 구현이다.

## TASK-011 — Provider별 비차단 Hook 설치와 복구

### Goal
사용자가 명시적으로 활성화했을 때 자동 보관을 제공한다.
### Dependencies
TASK-009, TASK-010.
### Blocked By
UC-001, UC-007.
### Scope
실제 지원 버전별 Stop/SubagentStop/Interrupt 등 필요한 command Hook만 연결한다. event neutral response·async·timeout·path validation, 기존 설정 병합·backup·제거, 실시간 context provenance를 구현한다. 이름이 비슷하다고 Provider Hook 설정을 공유하지 않는다.
### Files
`src/TaskTokenMeter.Cli/Hooks/`, `packaging/hooks/`, `tests/TaskTokenMeter.IntegrationTests/HookTests.cs`, `docs/hooks.md`.
### Validation
테스트용 Provider 환경에서 성공·에러·timeout·중단·실행 파일 누락에도 사용자 작업이 계속된다. Codex Stop stdout 계약을 지킨다. 설치/제거 후 기존 unrelated Hook이 동일하며 모델 추가 호출·context 주입이 없다. 실제 사용자 설정은 검증용 fixture로 대체한다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
Provider별 종료/출력 계약과 설정 보존을 함께 검증해야 한다.

## TASK-012 — 선택적 로컬 비용 추정

### Goal
사용자가 UC-006=B를 선택했을 때만 모델·TTL별 추정 비용을 제공한다.
### Dependencies
TASK-007, TASK-008, TASK-010.
### Blocked By
UC-006. A 선택 시 Deferred로 전환하고 MVP 필수 의존에서 제거한다. C 선택 시 별도 환산 계약 승인 전 시작하지 않는다.
### Scope
버전·통화·유효일·모델별 단가표, call 단위 계산, unknown pricing/TTL의 N/A, 알려진 부분합, 출처와 추정임을 표시한다. 과거 호출에 오늘의 가격을 무조건 적용하지 않는다.
### Files
`src/TaskTokenMeter.Core/Pricing/`, `tests/TaskTokenMeter.UnitTests/PricingTests.cs`, `docs/pricing.md`.
### Validation
혼합 모델·TTL·가격 공백·알 수 없는 모델·reasoning 부분집합 fixture 통과. Processed에 단일 단가를 곱하지 않는다. 공식 가격 출처와 확인일을 기록하고 실제 billing과 동일하다고 주장하지 않는다.
### Agent
Codex
### Model
gpt-5.6-terra
### Reasoning Level
Medium
### Reason
범위가 선택된 뒤에는 표와 수식 중심이므로 중간 추론 수준이 적합하다.

## TASK-013 — 전체 흐름·성능·배포 검증

### Goal
두 Provider의 지원 범위와 배포 가능한 품질을 증거로 확인한다.
### Dependencies
TASK-010, TASK-011. UC-006=B이면 TASK-012도 필수. UC-001=C이면 TASK-002에서 Hook 관련 의존과 출시 기준을 먼저 변경한다.
### Blocked By
UC-002 및 앞선 작업의 모든 미해결 차단.
### Scope
합성 E2E와 승인된 실환경 smoke, 동일 scope 참조 비교, 성능 30회 측정, 8 writer, 깨끗한 Windows 설치·runtime 부재·native dependency 검증, support matrix를 작성한다.
### Files
`tests/TaskTokenMeter.IntegrationTests/EndToEndTests.cs`, `packaging/`, `docs/validation/acceptance.md`, `docs/validation/performance.md`.
### Validation
FR-01~12와 design Success Criteria에 pass/fail/evidence를 연결한다. 20 MB/100,000행 p95·RSS와 Hook 수락 시간을 실제 측정한다. 기준 미달이면 원인과 수정 작업을 열고 합격 처리하지 않는다. 두 Provider 중 하나 미검증이면 전체 지원 완료라 하지 않는다.
### Agent
Codex
### Model
gpt-5.6-sol
### Reasoning Level
High
### Reason
관측 scope·환경·계측 비용을 종합하는 검증이며 단일 unit test로 대체할 수 없다.

## TASK-014 — 교차 리뷰와 구현 인수 문서

### Goal
정확성·개인정보·비차단 동작을 독립적으로 검토하고 다음 유지보수자가 재현할 수 있게 한다.
### Dependencies
TASK-013. TASK-012는 UC-006에서 정한 필수/Deferred 상태를 확인한다.
### Blocked By
해결되지 않은 구현 차단 및 Critical/High 리뷰 이슈.
### Scope
구현 diff, scope claim, dedup/null/lineage, 저장 회복·설정 보존, 민감 데이터 경로를 리뷰한다. 설치·제거·조회·지원 버전·측정 한계·복구 절차를 README에 작성한다. 가능하면 구현과 다른 에이전트 세션에서 리뷰한다.
### Files
`README.md`, `docs/validation/review.md`, `docs/validation/acceptance.md`, `docs/prepare/plan.md`의 완료 기록.
### Validation
Critical/High 미해결 이슈가 없고 문서 명령이 배포 산출물에서 재현된다. 사용자 결정·지원 범위·검증 결과가 일치한다. 사용자 요청 없는 push·publish·실사용 Hook 설치는 하지 않는다.
### Agent
Codex
### Model
gpt-6-astra
### Reasoning Level
High
### Reason
핵심 오류와 개인정보 누출을 독립 검토하고 전체 결과를 통합하는 작업이다.

## 준비 작업의 완료 점검

| 항목 | 상태 |
|---|---|
| docs/ideas 전체 두 문서 분석 | 완료 |
| design / architecture / user-confirm 작성 | 완료 |
| 그래픽 UI 필요 여부 판단 | 불필요, CLI 예시 포함 |
| plan을 마지막에 작성 | 완료 |
| 모든 TASK에 Agent / Model / Reasoning Level / Reason | 명시 |
| Pending 결정과 Blocked By 연결 | 명시 |
| 제품 구현·실제 Hook 설치 | 수행하지 않음 |
| 실제 Provider 로그 정확성·성능 검증 | 구현 계획에 포함, 이번 준비에서 수행하지 않음 |

문서 준비 완료는 사용자 결정이나 제품 정확성 검증 완료를 의미하지 않는다. 즉시 시작 가능한 후속 작업은 TASK-001의 계약 조사이며, 제품 구현은 결정 반영 게이트를 통과한 뒤 시작한다.
