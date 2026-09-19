# User Confirmation — 구현 전 결정

작성일: 2026-09-19

이 문서는 준비 작업의 산출물이다. 아래 Pending 항목에 답하지 않아도 문서 준비는 완료할 수 있지만, 영향을 받는 제품 구현은 시작하지 않는다. 추천안은 사용자 승인으로 간주하지 않는다.

상태는 `Pending`(결정 필요), `Confirmed`(근거가 있는 결정), `Deferred`(후속 범위)만 사용한다. 변경 시 선택, 날짜, 결정 근거를 기록하고 해당 설계·계획을 함께 갱신한다.

## 기존 확정 원칙

| ID | 결정 | 상태 | 근거 |
|---|---|---|---|
| D-001 | 외부 로컬 프로그램으로 계산, LLM 계산 금지 | Confirmed | 원안 12절 |
| D-002 | Claude Code + Codex Adapter 지원 | Confirmed | 원안 12절 |
| D-003 | Provider 실제 usage 우선, native와 normalized 동시 보존 | Confirmed | 원안 12절 |
| D-004 | 개별 작업 소비량이 목적, MVP는 Turn 단위 | Confirmed | 원안 8·12절 |
| D-005 | Hook 실패가 개발 작업을 막지 않음 | Confirmed | 원안 9절 |
| D-006 | Skill은 선택적 CLI 호출 인터페이스 | Confirmed | 원안 12절 |

## 결정 요약

| ID | 결정 | 추천 | 상태 | 직접 영향 TASK |
|---|---|---|---|---|
| UC-001 | Turn 경계·수집 전략 | 로그 ID 중심, 조회 prototype 후 비차단 Hook MVP | Pending | 002, 003, 005, 006, 007, 011 |
| UC-002 | 구현 언어·배포·OS | C#/.NET, Windows 우선 self-contained CLI | Pending | 002, 003, 013 |
| UC-003 | 저장 위치·workspace 구분 | 전역 로컬 저장, worktree별 workspace | Pending | 002, 009, 010 |
| UC-004 | Ledger 형식 | SQLite | Pending | 002, 009 |
| UC-005 | child·미관측 usage의 MVP 범위 | 귀속 가능한 child 포함, 미귀속 공개 | Pending | 002, 007 |
| UC-006 | 비용 환산 범위 | Later 유지, MVP는 지표 정의·한계 우선 | Pending | 002, 012 |
| UC-007 | 메타데이터·보관·개인정보 | 본문 제외 최소 metadata + opt-in hash/label | Pending | 002, 009, 010, 011 |
| UC-008 | CLI 계약·세션 선택 | 명시 선택자 + 유일 후보만 자동 선택 | Pending | 002, 010 |

위 표는 직접 차단만 요약한다. 선행 TASK가 차단되면 후속 TASK에도 전파되며, 자세한 의존 관계는 [plan.md](plan.md)에 있다.

## UC-001 — Turn 경계와 Hook 도입 순서

### Context

원안은 UserPromptSubmit/Stop 사이를 Turn으로 측정한다. 검토 의견은 로그의 promptId/turn_id를 기준으로 재구축하고 Hook은 재집계 트리거로만 쓰자고 제안한다. 검토 의견이 원안을 대체하도록 승인된 기록은 없다.

### Options

- **A. 로그 ID 중심 + 조회 prototype → Hook MVP:** Hook 누락·중단·늦은 child 복구에 유리하다. 내부 로그 형식과 root lineage 검증이 필요하다.
- **B. Hook 중심 경계:** 원안의 흐름과 가깝다. 설치 전 기록·누락된 종료·백그라운드 작업의 복구 규칙과 event journal이 추가로 필요하다.
- **C. 첫 출시를 조회 전용으로 제한:** 설치가 가장 쉽다. 자동 보관 요구를 충족하지 못하므로 출시 범위를 명시적으로 축소해야 한다.

### Recommendation

A. Hook 없는 조회는 실험 단계이며, 자동 보관을 포함한 MVP를 생략한다는 의미가 아니다. source의 턴 ID를 검증하지 못한 Provider는 B로 조용히 fallback하지 않고 계약을 재검토한다.

### Impact

architecture의 Data Flow·State·Adapter, plan의 수집·CLI·Hook 작업을 갱신한다. B 선택 시 Hook 누락 복구와 시작/종료 journal 설계 작업을 추가한다. C 선택 시 TASK-011을 후속으로 옮기고 MVP 인수 조건을 변경한다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-002 — 제품 언어, 배포 방식, 지원 OS

### Context

원안은 Python/PowerShell/.NET 등을 열어 두었으며 최종 선택을 하지 않았다. 검토 의견은 Python prototype와 C# 배포를 제안했다.

### Options

- **A. C#/.NET 제품 + Windows 우선:** 정식 solution과 타입 모델, exe 배포에 적합하다. SDK와 native SQLite 패키징 검증이 필요하다.
- **B. Python 제품:** JSONL 조사에서 제품까지 재사용하기 쉽다. interpreter 또는 별도 패키징 환경을 관리해야 한다.
- **C. 처음부터 Windows/macOS/Linux 지원:** 사용 범위가 넓다. Hook·경로·설치·CI 검증 행렬과 배포 비용이 커진다. 언어도 함께 정해야 한다.

### Recommendation

A. 먼저 Windows x64 배포를 검증하고 다른 RID는 요청 시 추가한다. 지원 중인 .NET LTS와 패키지 버전은 구현 시작 시 고정한다. 조사 스크립트는 허용하되 제품을 PowerShell 단일 스크립트로 대체하지 않는다.

### Impact

architecture의 Technology Stack·Directory·Build, 모든 코드 경로와 테스트/배포 계획을 변경한다. 실제 제품 언어가 결정되기 전 프로젝트 scaffold를 만들지 않는다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-003 — 저장 위치와 workspace 경계

### Context

원안은 workspace-local을 우선 검토하고, 검토 의견은 worktree/서브모듈 환경 때문에 전역 저장을 추천한다.

### Options

- **A. 사용자 전역 로컬 저장:** 작업 트리에 파일이 생기지 않고 교차 workspace 조회 기반이 생긴다. 경로 이동·삭제·데이터 관리 UI/명령 계약이 필요하다.
- **B. workspace `.token-meter/`:** 데이터 위치가 명확하고 프로젝트별 제거가 쉽다. worktree별 데이터 분산과 Git 제외 설정이 필요하다.
- **C. 두 방식 모두 제공:** 선택 폭은 넓지만 migration과 중복 수집 정책을 동시에 구현해야 한다.

### Recommendation

A. Windows 기본 root는 `%LOCALAPPDATA%/TaskTokenMeter/`, 사용자가 로컬 data directory를 재정의할 수 있게 한다. worktree와 submodule은 가장 가까운 Git root로 구분한다. 같은 remote라고 자동 병합하지 않는다. workspace export는 후속 범위다.

### Impact

Discovery·Configuration·Workspace 모델·저장 및 CLI 테스트를 갱신한다. 저장 위치 변경 시 이동·중복 탐지 절차가 필요하다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-004 — Ledger 저장 형식

### Context

단일 JSONL append 예시는 동시 writer와 뒤늦게 바뀌는 Turn 결과에 대한 갱신 계약이 없다.

### Options

- **A. SQLite:** unique key·transaction·upsert가 명확하다. native dependency·migration·로컬 디스크 제한을 검증해야 한다.
- **B. 세션별 JSONL:** 사람이 보기 쉽고 DB 의존이 없다. revision log·compaction·atomic replace·동시 잠금 규칙을 직접 구현해야 한다.
- **C. 단일 JSONL + 전역 writer:** 파일은 하나지만 큐나 lock이 병목/복구 부담이 될 수 있다.

### Recommendation

A. 파일 읽기는 transaction 밖, 짧은 write transaction과 revision 비교를 사용한다. 네트워크/동기화 공유 디렉터리를 기본 저장소로 쓰지 않는다.

### Impact

Storage interface는 유지할 수 있으나 architecture의 저장·복구·migration 및 TASK-009 검증을 선택안에 맞게 변경한다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-005 — 서브에이전트 포함과 측정 한계

### Context

검토 의견은 child usage가 큰 비중이며 main-only는 과소 집계할 수 있다고 보고했다. 반면 부가 호출은 transcript에 없을 수 있고 모든 child의 root 귀속도 보장되지 않는다.

### Options

- **A. 귀속 가능한 child를 포함:** 작업 소비량에 가까워진다. 중복·root 포함 관계·귀속 실패 처리가 필요하다.
- **B. main-only로 첫 출시:** 구현 범위가 작다. CLI 전반에 main-only를 명시해야 하며 전체 작업 토큰이라고 홍보할 수 없다.
- **C. OTel 등 보조 소스까지 MVP에 포함:** 관측 범위를 넓힐 수 있다. collector 설치·소스 간 identity·개인정보 범위가 크게 늘어난다.

### Recommendation

A. unknown child를 시간으로 억지 귀속하지 않고 개수/한계를 표시한다. coverage는 비교 가능한 독립 분모가 있을 때만 제공한다. OTel 수집은 후속 단계다.

### Impact

root 집계 계약, UX scope 표시, source discovery, fixture, 인수 기준을 갱신한다. B이면 child 샘플·FR-12 범위를 변경하고 C이면 별도 설계 작업을 추가한다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-006 — 비용 추정을 MVP에 포함할지

### Context

원안은 비용을 Later로 두었으나 검토 의견은 Processed 합계가 비교를 왜곡할 수 있어 MVP 승격을 권고했다. 비용은 정확한 모델·TTL·가격 유효일 정보도 필요하다.

### Options

- **A. Later 유지:** 핵심 계측 검증에 집중한다. MVP에서 비용 효율 비교를 제공하지 않는다.
- **B. 로컬 단가표 기반 추정 포함:** 비용 내역을 볼 수 있다. 가격표 버전·모델 alias·TTL 미확정 및 subscription과의 차이를 처리해야 한다.
- **C. 입력 환산 지표만 포함:** 표시가 단순해질 수 있지만 환산 가중치가 또 하나의 오해를 만들 수 있다.

### Recommendation

A. 원안의 작은 MVP를 유지하고 `Processed는 비용 점수가 아님`을 명시한다. B 선택 시 호출별 model/TTL에 유효한 가격표로 계산하고, 한 항목이라도 가격/의미가 없으면 전체 추정은 N/A와 알려진 부분합으로 구분한다. 실시간 가격 수집은 별도 범위다.

### Impact

design 범위·CLI 출력, architecture에 pricing component, TASK-012 포함 여부를 변경한다. B를 택하면 TASK-012가 출시 게이트다. A/C도 선택 자체는 기록해야 한다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-007 — 수집 metadata와 보관 정책

### Context

장기 비교를 위해 실행 시점의 환경 정보가 유용하지만 경로·브랜치·플러그인·라벨은 민감할 수 있다. 과거 기록을 읽을 때 현재 설정을 과거 설정으로 보존하면 비교가 틀린다.

### Options

- **A. 최소 metadata + opt-in 확장:** usage, 로컬 ID, source provenance, 로그가 제공한 model/effort/clientVersion만 기본 수집. 해시·git HEAD·라벨은 명시적 설정에서만 수집한다. 데이터 노출 범위를 줄이나 과거 비교 정보가 부족할 수 있다.
- **B. hash/label도 기본 수집:** 비교 준비는 쉽다. 사용자 동의와 보관·삭제 기준이 더 중요하다.
- **C. usage만 단기 보관:** 최소 수집이다. 재검증과 환경별 비교가 제한된다.

### Recommendation

A. 대화/툴 인자/.env는 어느 옵션에서도 저장하지 않는다. ledger는 기본 자동 삭제 없이 명시적 purge, 진단은 14일 또는 총 10 MB를 잠정값으로 제안한다. 삭제 시 집계도 잃는다는 점을 표시한다. Hook 실시간 context와 과거 로그 context의 origin/capturedAt을 구분한다.

### Impact

ContextSnapshot, native usage 추출 정책, 진단 rotation, retention/purge와 개인정보 검증을 갱신한다. 보관 기간·크기·opt-in 범위도 함께 확정해야 한다.

### User Decision

**Pending** — 선택·보관 기간·결정일 미기록.

## UC-008 — CLI 조회 계약과 세션 모호성

### Context

원안은 `current`, 검토 의견은 `last`와 `turns`를 제안했다. 둘은 진행 중/종료된 Turn에서 의미가 다르다. 여러 Provider 세션이 같은 workspace에서 동시에 실행될 수 있다.

### Options

- **A. current/last 분리 + 명시 선택자:** 의미가 명확하고 스크립트화가 쉽다. 여러 후보일 때 사용자가 ID를 지정해야 한다.
- **B. workspace에서 가장 최근 세션 자동 선택:** 입력은 짧지만 다른 작업의 값을 조회할 수 있다.
- **C. 항상 대화형 선택:** 사람이 쓰기는 편하다. Hook/CI/JSON 파이프에서는 별도 규칙이 필요하다.

### Recommendation

A. `current`는 최신 root Turn(진행 중 포함), `last`는 최신 terminal Turn, `turns`는 session 내부 목록이다. 모호하면 후보와 재실행 명령을 보여준다. 원본이 없으면 보관값의 시점을 표시한다. `--json`, `--strict`는 architecture의 계약을 따른다.

### Impact

FR-01~05·CLI 예시·exit code·선택자 테스트·설치 안내를 함께 갱신한다.

### User Decision

**Pending** — 선택·결정일 미기록.

## UC-009 — Task 묶기와 통계

### Context / Options

수동 `task start/end`는 사용자 목적이 명확하지만 추가 입력이 필요하다. 브랜치 기본 그룹은 자동이지만 한 브랜치의 여러 작업·여러 worktree를 구별하지 못할 수 있다. 규칙 기반 추론은 편리하지만 예외가 많다.

### Recommendation / Impact

Turn 계측이 안정된 뒤 수동 Task와 브랜치 후보를 비교한다. branch를 MVP의 Task identity로 고정하지 않는다. Task 테이블·history/stats는 후속 기획에서 추가한다.

### User Decision

**Deferred** — 원안의 Later 분류를 유지. MVP 차단 없음.

## UC-010 — 그래픽 UI와 보조 수집기

### Context / Options

터미널 CLI만으로 현재 목표를 충족한다. TUI/웹 Dashboard는 탐색을 돕지만 별도 사용자 흐름과 실행·배포가 필요하다. OTel 수집기는 관측 범위를 넓히나 설치·운영 비용이 추가된다.

### Recommendation / Impact

CLI를 우선한다. Dashboard/TUI를 요청할 때 taste·impeccable을 적용한 UI 시안 3종을 만들고 다시 선택한다. OTel은 별도의 source identity와 개인정보 설계를 선행한다.

### User Decision

**Deferred** — 그래픽 UI는 원안의 Later, OTel은 채택되지 않은 검토 제안. MVP 차단 없음.

## 결정을 기록하는 방법

예: `UC-001=A, UC-002=A, ...`로 선택을 전달할 수 있다. 이는 입력 형식 예시이며 실제 결정이 아니다. 결정 이후 각 User Decision에 선택·날짜·근거를 기록하고, TASK-002에서 전체 문서의 범위·차단 관계를 동기화한다.
