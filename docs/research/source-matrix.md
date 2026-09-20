# Provider 조사 Source Matrix

조사일: 2026-09-20
설치 기준: Claude Code `2.1.278`, Codex CLI `0.153.4`, ccusage `20.0.18`

이 문서는 [provider-contracts.md](provider-contracts.md)의 주장과 근거를 추적한다. `provider-contracts.md`의 상태 정의를 그대로 사용한다. 로컬 측정은 필드명·개수·숫자 관계만 집계했으며 원문 행, prompt/message/tool 내용, 실제 ID, 개인 경로를 저장하지 않았다.

## Source catalog

| Source ID | 버전/확인일 | 근거 | 다루는 범위 | 안정성·제한 |
|---|---|---|---|---|
| `ANT-HOOK` | 현재 문서, 2026-09-20; 설치 2.1.278 | [Claude Hooks reference](https://code.claude.com/docs/en/hooks) | Hook lifecycle, `session_id`, `prompt_id`, resume/fork, Stop/SubagentStop, stdout/exit/async | Hook 공개 계약. transcript 내부 형식은 보장하지 않음 |
| `ANT-OTEL` | 현재 문서, 2026-09-20; 설치 2.1.278 | [Claude Monitoring](https://code.claude.com/docs/en/monitoring-usage) | `prompt.id`, request/message correlation, API usage, query source, resume ordering | transcript join은 문서 자체가 version-specific internal이라고 경고 |
| `OAI-HOOK` | 현재 문서, 2026-09-20; 설치 0.153.4 | [Codex Hooks](https://developers.openai.com/codex/hooks) | `session_id`, `turn_id`, Stop/SubagentStop/Interrupt, stdout, async | release behavior 기준. linked main schema는 현재 release보다 앞설 수 있음 |
| `OAI-APP` | 현재 문서, 2026-09-20 | [Codex App Server](https://developers.openai.com/codex/app-server) | thread resume/fork, turn status/interrupt, token usage notification | rollout `token_usage_record`의 세 객체 의미를 보장하지 않음 |
| `CLAUDE-LOCAL` | Claude Code 2.1.278, 최근 transcript 100개 | 로컬 read-only 구조 집계 | promptId 존재율, request/message alias, snapshot revision, UUID replay, cache TTL, child promptId, notification 개수 | 개인 표본. 정확한 버전/shape 외 일반화 금지 |
| `CODEX-LOCAL` | Codex CLI 0.153.4, 최근 rollout 100개 | 로컬 read-only 구조 집계 | usage kind 관계, ID lineage, root scope, numeric invariant | 개인 표본. 원문/ID/경로 미보존. active file 재조회로 lineage snapshot은 2,470 record, invariant snapshot은 2,457 record임 |
| `SESSION-REPORT` | installed version `unknown`, commit `c447c3207a425bc4e2a0d068435f64b0477ae981` | 설치된 `analyze-sessions.mjs`, [upstream skill](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/session-report/skills/session-report/SKILL.md) | Claude transcripts/subagents, request dedupe, prompt report | Apache-2.0. 참조 구현 정책이며 Provider 계약 아님. prompt preview 포함 가능 |
| `CCUSAGE` | installed 20.0.18 | 설치 도움말, [upstream v20.0.18](https://github.com/ccusage/ccusage/tree/v20.0.18) | Claude/Codex local day/week/month/session reports | MIT. Turn/prompt oracle 아님; 비용·grouping scope 분리 필요 |
| `IDEA-MEASURE` | Claude 2.1.277, Codex 0.153.4; 2026-09-19 보고 | [아이디어 검토](../ideas/task-token-meter-claude-feedback.md) | 더 넓은 이전 표본의 중복·total-only·coverage 결과 | 이번 TASK에서 원시 표본을 재사용하지 않은 secondary evidence |

## Claim matrix

| ID | 주장 | Provider/버전 | 근거 | 상태 | 재현 절차 | 구현 결과 |
|---|---|---|---|---|---|---|
| `C-CLAUDE-001` | Hook `prompt_id`는 현재 prompt UUID이며 OTel `prompt.id`와 일치 | Claude 2.1.196+ / 설치 2.1.278 | `ANT-HOOK`, `ANT-OTEL` | `documented` | prompt 본문 없이 Hook/OTel equality boolean 확인 | Hook DTO nullable `prompt_id` |
| `C-CLAUDE-002` | Hook `prompt_id == transcript.promptId` | Claude 2.1.278 | 직접 명시 없음 | `unknown` | 같은 prompt의 Hook ID와 persisted user entry ID equality만 기록 | 직접 join 금지; Hook은 session rescan |
| `C-CLAUDE-003` | user `promptId`로 Turn을 열 수 있으나 assistant usage에는 필드가 없음 | Claude 2.1.278 | `CLAUDE-LOCAL`: user 5,603/5,620, assistant usage 0/9,531 | `observed` | synthetic user→assistant chain과 missing-ID 반례 | parent/order resolver와 unattributed 상태 필요 |
| `C-CLAUDE-004` | API event `request_id`는 transcript `requestId`에 저장 | Claude current | `ANT-OTEL` | `documented`, transcript는 version-specific | OTel event와 transcript의 equality boolean | requestId primary alias |
| `C-CLAUDE-005` | requestId/message.id가 표본에서 1:1이고 message fallback이 필요 | Claude 2.1.278 | `CLAUDE-LOCAL`: 다대일/일대다 0, requestId 누락 15 | `observed` | alias 혼재 fixture | request primary, message fallback |
| `C-CLAUDE-006` | 한 API response가 여러 snapshot 행이고 output 최대 snapshot이 완전값 | Claude 2.1.278 | `CLAUDE-LOCAL`: multi-row 3,162, varying output 415, 감소 0; `SESSION-REPORT` | `observed` | 9→9→338 fixture, immutable conflict fixture | compatible snapshot만 최대 output 선택 |
| `C-CLAUDE-007` | resume/fork replay는 entry UUID로 중복 제거 가능 | Claude 2.1.278 | duplicate UUID 1,055; `SESSION-REPORT` | `observed` | 파일 간 동일 UUID와 다른 origin 반례 | lineage 안에서 UUID dedupe |
| `C-CLAUDE-008` | fork parent session을 Hook만으로 찾을 수 있음 | Claude 2.1.278 | `ANT-HOOK`에는 fork source만 있음 | `unsupported` | parent ID 부재 fixture | 별도 session + `fork_parent_unknown` |
| `C-CLAUDE-009` | cache creation total은 5m+1h breakdown과 일치 | Claude 2.1.278 | `CLAUDE-LOCAL`: 9,531/9,531, mismatch 0 | `observed` | 정상/불일치 TTL fixture | TTL 분리, total 중복 합산 금지 |
| `C-CLAUDE-010` | `thinking_tokens`가 normalized reasoning으로 안정 사용 가능 | Claude 2.1.278 | 필드 존재 7,215행, 공식 transcript 의미 미확인 | `unknown` | 포함 관계·공식 의미 확인 | native only, reasoning null |
| `C-CLAUDE-011` | child promptId는 main promptId로 귀속 가능 | Claude 2.1.278 | `CLAUDE-LOCAL`: child distinct 119/119 main match | `observed` | main/child 동일 ID fixture | 명시 ID가 있을 때 child 포함 |
| `C-CLAUDE-012` | task-notification은 직전 prompt에 속함 | Claude 2.1.278 | marker 49행, `SESSION-REPORT` 정책 | `unknown` | marker + explicit/missing promptId fixtures | 참조 정책은 쓰되 missing identity면 provisional |
| `C-CLAUDE-013` | Stop 시 transcript write가 지연될 수 있음 | Claude current | `ANT-HOOK`가 async lag 명시 | `documented` | Stop 직후/재조회 snapshot fixture | Stop을 final parse 시점으로 보지 않음 |
| `C-CLAUDE-014` | 사용자 interrupt 시 Stop은 실행되지 않음 | Claude current | `ANT-HOOK` | `documented` | interrupt smoke 또는 missing Stop fixture | Stop 부재를 completed로 해석 금지 |
| `C-CLAUDE-015` | neutral Meter Hook은 exit 0 + 빈 stdout | Claude current | `ANT-HOOK` stdout/exit/async | `documented` | stdout/exit matrix fixture | model context·continuation 생성 금지 |
| `C-CLAUDE-016` | 일반 async Hook은 시작 뒤 timeout으로 종료됨 | Claude current | `ANT-HOOK`는 timeout 미적용 명시 | `unsupported` | 장기 process smoke | Hook은 빠른 enqueue만 수행 |
| `C-CODEX-001` | Hook은 session_id와 Turn 이벤트의 turn_id를 제공 | Codex current / 설치 0.153.4 | `OAI-HOOK` | `documented` | typed payload fixture | Hook session rescan key |
| `C-CODEX-002` | Hook은 root_turn_id를 제공 | Codex current | `OAI-HOOK`에 없음 | `unsupported` | payload schema negative fixture | rollout에서만 root resolve |
| `C-CODEX-003` | rollout `usage`는 call delta | Codex 0.153.4 | `CODEX-LOCAL`: turn 전이 1,402/1,402, thread 전이 2,373/2,373 | `observed` | previous+delta=current fixture | detail/validation, snapshot과 합산 금지 |
| `C-CODEX-004` | `turn_token_usage`는 Turn 누적 snapshot | Codex 0.153.4 | `CODEX-LOCAL`: final==delta sum 1,056/1,061 | `observed` + 5 mismatch | 100→250, mismatch fixture | Turn authority + mismatch partial |
| `C-CODEX-005` | `thread_token_usage`는 thread/session 누적 snapshot | Codex 0.153.4 | `CODEX-LOCAL`: 연속 전이 2,373/2,373 | `observed` | 두 Turn 누적 fixture | session 진단만, Turn 합계 제외 |
| `C-CODEX-006` | root snapshot은 child-inclusive | Codex 0.153.4 | resolvable child group 0/44 inclusive | `unsupported` for this shape | main-only/inclusive 쌍 fixture | capability를 main-only로 설정 |
| `C-CODEX-007` | root snapshot은 main-only | Codex 0.153.4 | resolvable child group 44/44 main-only | `observed` | root+child 고유 execution 합 | child snapshot을 한 번 추가 |
| `C-CODEX-008` | `session_id`는 root, `thread_id`는 실행 thread | Codex 0.153.4 | ID equality 2,470/2,470; child 89, nested 23 | `observed` | nested parent fixture | root key에 session_id 포함 |
| `C-CODEX-009` | `root_turn_id`만으로 root session을 알 수 있음 | Codex 0.153.4 | nested child와 별도 session/thread 관계 | `unsupported` | 같은 root_turn_id/다른 session fixture | session_id 없이 귀속 금지 |
| `C-CODEX-010` | cached는 input, reasoning은 output의 부분집합 | Codex 0.153.4 | 7,371 usage objects, 위반 0 | `observed` | 정상/위반 fixture | fresh 차감, reasoning 별도 detail |
| `C-CODEX-011` | total은 input+output | Codex 0.153.4 current 표본 | 7,371/7,371 일치 | `observed` | total mismatch fixture | mismatch invalid |
| `C-CODEX-012` | total-only는 현재 일반 형식 | Codex 0.153.4 | current 0건, `IDEA-MEASURE` 이전 14건 | `unknown` | total-only 합성 fixture | native total only, 0 채움 금지 |
| `C-CODEX-013` | nonzero cache write의 포함 관계 | Codex 0.153.4 | current 0건, 공식 의미 없음 | `unknown` | nonzero 합성 + future real sample | native preserve, processed에 추가 금지 |
| `C-CODEX-014` | main Interrupt Hook은 turn_id를 제공하며 중단을 되돌릴 수 없음 | Codex current | `OAI-HOOK` | `documented` | Interrupt payload/stdout fixture | same Turn rescan, no block |
| `C-CODEX-015` | subagent Interrupt Hook이 실행됨 | Codex current | `OAI-HOOK`가 명시적으로 부정 | `unsupported` | child interrupt negative fixture | child state를 Hook만으로 terminal 처리 금지 |
| `C-CODEX-016` | Stop/SubagentStop plain stdout이 neutral | Codex current | `OAI-HOOK`가 invalid 명시 | `unsupported` | stdout matrix fixture | exit 0 + `{}` 사용 |
| `C-CODEX-017` | fork 결과는 새 thread id와 sessionId/forkedFromId를 가짐 | Codex App Server current | `OAI-APP` | `documented` | synthetic RPC fixture | lineage metadata 보존 |
| `C-CODEX-018` | rollout fork replay의 원실행 중복 규칙 | Codex 0.153.4 | 공식 의미 없음, forked_from_id 5개 관측 | `unknown` | origin evidence 있음/없음 fixture | 모호하면 dedupe·합산 모두 금지 |
| `C-TOOL-001` | session-report는 Claude prompt-level 참고값을 제공 | commit c447c320… | `SESSION-REPORT` | `observed` | synthetic copy에서 analyzer 실행 | Claude parser 교차 검증 전용 |
| `C-TOOL-002` | session-report license/version | installed commit c447c320… | local registry/LICENSE | `observed` | registry fields와 LICENSE 확인 | Apache-2.0, package version unknown 기록 |
| `C-TOOL-003` | ccusage는 Turn oracle | ccusage 20.0.18 | 설치 help와 `CCUSAGE` | `unsupported` | help command surface 확인 | 동일 scope session total만 참고 |
| `C-TOOL-004` | ccusage license/version | ccusage 20.0.18 | binary `--version`, upstream tag | `observed` | `ccusage --version`, tag LICENSE/README | MIT, version pin |

## Oracle scope matrix

| 도구/source | Claude main | Claude child | Claude auxiliary | Codex root | Codex child | Turn/prompt | 비용 | 개인정보 주의 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| Task Token Meter 목표 | 포함 | 명시 귀속 시 포함 | 로그에 있으면 native/unattributed | 포함 | 명시 귀속 시 포함 | root Turn | Later | 본문 저장 금지 |
| Claude transcript | 포함 | 별도 파일 | 일부 누락 가능 | 해당 없음 | 해당 없음 | user promptId 후보 | 없음 | 본문 포함 |
| Claude OTel | 포함 | query_source로 구분 | auxiliary 구분 | 해당 없음 | 해당 없음 | prompt.id | 추정 cost 포함 | exporter 설정과 민감 속성 주의 |
| session-report | 포함 | 포함 | background fork 일부 미귀속 | 해당 없음 | 해당 없음 | prompt 상위 목록 | 계산값 포함 | prompt preview가 산출될 수 있음 |
| Codex rollout | 해당 없음 | 해당 없음 | 해당 없음 | 포함 | 별도 thread | turn/root_turn | 없음 | message/tool 본문 포함 |
| ccusage 20.0.18 | session total | 도구 정책에 따름 | 도구 정책에 따름 | session total | 도구 정책에 따름 | Turn 없음 | 있음 | JSON을 repository에 저장 금지 |

도구 간 합계는 source extent, 시간 범위, model, token metric, child/auxiliary scope가 같을 때만 비교한다. 비교 분모가 독립적이지 않거나 scope가 다르면 coverage는 N/A다.

## 재현 명령 범주

실제 명령 문자열과 사용자 경로는 산출물에 남기지 않는다. 검증자는 다음 범주를 로컬 read-only로 재현한다.

| 검사 | 입력 | 허용 출력 |
|---|---|---|
| 설치 버전 | `claude --version`, `codex --version`, `ccusage --version` | 버전 문자열 |
| Hook/App 계약 | 위 공식 문서 | 필드·이벤트·최소 버전 |
| Claude 구조 | 최근 N transcript line streaming parse | field name, count, equality/mismatch count |
| Codex 구조 | 최근 N rollout line streaming parse | record shape, count, invariant/mismatch count |
| session-report | plugin registry, analyzer source, LICENSE | commit, version metadata, license, algorithm summary |
| ccusage | `--help`, upstream pinned tag | command scope, version, license |

금지 출력은 원문 JSON line, ID 값, prompt/message/tool text, source absolute path, 환경 변수 값, token/secret이다.

## TASK-004 전달 체크리스트

- 모든 fixture에 위 Claim ID를 하나 이상 연결한다.
- `documented`와 `observed`를 서로 대체하지 않는다.
- Claude alias conflict와 Codex delta/snapshot mismatch를 정상 합계로 숨기지 않는다.
- `rootScope`의 main-only/child-inclusive/unknown 세 capability를 각각 만든다.
- total-only, nonzero cache write, 누락 ID, interrupt, late child, resume/fork를 포함한다.
- Hook stdout fixture는 Claude empty, Codex Stop/SubagentStop `{}`, Codex Interrupt empty를 검증한다.
- 실제 원문이나 개인 ID가 fixture에 들어가지 않았는지 별도 secret/content scan을 실행한다.
