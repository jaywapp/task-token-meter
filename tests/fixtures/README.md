# 합성 Golden fixture와 수동 oracle

이 디렉터리는 Claude Code `2.1.278`과 Codex CLI `0.153.4`의 현재 관측 형식 및 명시적인 합성 capability를 검증하기 위한 최소 입력과 독립 expected 값을 담는다. 모든 데이터는 `synthetic: true`인 합성 자료다. 실제 prompt, message, tool 본문, 사용자 ID, 로컬 경로, 토큰, 키를 포함하지 않는다.

`expected/*.json`은 parser 실행 결과를 복사한 snapshot이 아니다. 각 파일의 `manualCalculation`에 적힌 산식과 [provider-contracts.md](../../docs/research/provider-contracts.md), [source-matrix.md](../../docs/research/source-matrix.md)의 Claim ID만으로 사람이 계산한 oracle이다. Adapter 테스트는 `inputFiles`를 fixture root 기준 상대 경로로 해석하고, 모든 입력을 정확히 한 expected 파일에 연결해야 한다.

## Oracle 필드 규칙

- `providerVersion`, `recordShape`, `synthetic`, `claimIds`는 적용 범위를 고정한다. `synthetic-future-version`과 `synthetic-unknown-version`은 실제 지원 버전 주장이 아니라 capability 반례다.
- `nativeUsage`는 선택된 authoritative Provider 숫자 또는 검증 때문에 보존한 원 관측 숫자다. 자유 문자열 본문은 보존하지 않는다.
- `normalizedUsage`는 손계산 projection이다. Claude는 `inputTotal = uncachedInput + cacheRead + cacheWrite`, Codex는 `uncachedInput = inputTotal - cacheRead`를 쓴다. `processedTokens = inputTotal + output`이며 Codex cache write와 reasoning을 다시 더하지 않는다.
- 숫자 `0`은 실제로 관측된 영값이다. `null`은 필드가 없거나 의미를 확정할 수 없어 계산하지 않은 값이다. `quality: provisional`은 값이 바뀔 수 있거나 의미가 미확인인 경우, `partial`은 완전한 범위를 만들 수 없는 경우, `invalid`는 필드 충돌 또는 불변식 위반, `unsupported`는 필요한 identity/capability가 없는 경우다.
- `membership`은 root Turn과 고유 execution 사이의 귀속 근거다. child-inclusive snapshot의 child는 `attributed-already-in-root`로 표시하며 다시 더하지 않는다.
- `knownSubtotal`은 모르는 관측을 0으로 바꾸지 않고 현재 확실한 부분만 보여준다. 완전 합계를 뜻하지 않는다.
- `source-integrity.json`의 손상행과 tail은 JSON 파일 자체를 깨뜨리지 않기 위해 `rawFragment` 문자열로 감싼다. 테스트 harness가 이 문자열을 source line으로 materialize해 parser에 전달한다.

## Fixture 매핑과 수동 산식

| Fixture ID | 목적 | 입력 | Expected | Claim ID | 핵심 수동 산식 |
|---|---|---|---|---|---|
| `claude-two-turn-streaming-alias` | 2턴, 9→9→338 snapshot, request/message alias 혼재, 모델 변경 | `claude/two-turn-streaming-alias.jsonl` | `expected/claude-two-turn-streaming-alias.json` | C-CLAUDE-003/004/005/006/009/010 | T1 `10+20+5+338=373`; T2 `4+30+6+25=65`; session `438` |
| `claude-alias-conflict` | 같은 alias의 input/model 충돌을 격리 | `claude/alias-conflict.jsonl` | `expected/claude-alias-conflict.json` | C-CLAUDE-005/006 | `10≠11`, model A≠B이므로 합계 `null`; 필드별 max 금지 |
| `claude-cross-file-replay` | 파일간 UUID replay와 logical call replay 중복 제거 | `claude/cross-file-replay/source-a.jsonl`, `source-b.jsonl` | `expected/claude-cross-file-replay.json` | C-CLAUDE-005/006/007 | UUID 중복 제거 후 output max `15`; `3+4+0+15=22` |
| `claude-missing-prompt-meta` | promptId 없는 human, tool result/meta/compact가 Turn을 열지 않음 | `claude/missing-prompt-meta.jsonl` | `expected/claude-missing-prompt-meta.json` | C-CLAUDE-002/003 | Turn `0개`; usage `2+3=5`는 unattributed |
| `claude-main-child` | main/child 동일 promptId, child assistant promptId 누락 | `claude/main-child/main.jsonl`, `child.jsonl` | `expected/claude-main-child.json` | C-CLAUDE-003/011 | main `15` + child `9` = root `24` |
| `claude-late-child` | task notification, pending child, 동일 root key의 revision 갱신 | `claude/late-child/revision-1-main.jsonl`, `revision-2-child.jsonl`, `hooks.json` | `expected/claude-late-child.json` | C-CLAUDE-011/012/013 | rev1 `20` provisional; rev2 `20+10=30` observed |
| `claude-resume-fork` | resume UUID dedupe, fork 복사, alias 우연 충돌, parent 미확인 | `claude/resume-fork/session-main.jsonl`, `session-resume.jsonl`, `session-fork.jsonl`, `session-events.json` | `expected/claude-resume-fork.json` | C-CLAUDE-007/008 | resume `10+11=21`; fork UUID 복사 `0`, 새 entry `11` |
| `claude-cache-ttl` | 5m/1h 정상·불일치, thinking detail의 unknown 의미 | `claude/cache-ttl.jsonl` | `expected/claude-cache-ttl.json` | C-CLAUDE-009/010 | 정상 `10+20=30`, processed `47`; 불일치 `30≠31`, known subtotal `17` |
| `claude-lifecycle` | Stop 누락, StopFailure, Stop 뒤 transcript tail lag | `claude/lifecycle.json` | `expected/claude-lifecycle.json` | C-CLAUDE-013/014 | no Stop `3` provisional; failure `5`; tail rev1 `5` → rev2 `12` |
| `claude-hook-stdout` | 빈 stdout neutral 계약과 context/block 금지 | `claude/hook-stdout.json` | `expected/claude-hook-stdout.json` | C-CLAUDE-015/016 | exit `0` + stdout byte `0`만 neutral |
| `claude-source-integrity` | 중간 손상행과 미완성 tail에서 부분합 보존 | `claude/source-integrity.json` | `expected/claude-source-integrity.json` | C-CLAUDE-003/013 | 유효행 `3+2+0+4=9`; 손상 2건은 unknown count |
| `codex-two-turn-snapshots` | call delta, 100→250 turn snapshot, 누적 thread snapshot, 2턴 | `codex/two-turn-snapshots.jsonl` | `expected/codex-two-turn-snapshots.json` | C-CODEX-003/004/005/010/011 | T1 delta `100+150=250`; T2 `50`; thread `300`은 Turn에 미가산 |
| `codex-root-main-only` | main-only root와 child 두 개의 고유 실행 합집합 | `codex/root-main-only/main.jsonl`, `child-a.jsonl`, `child-b.jsonl` | `expected/codex-root-main-only.json` | C-CODEX-006/007/008 | `120+40+25=185`; fresh `150-55=95` |
| `codex-root-child-inclusive` | child-inclusive capability에서 child 재가산 금지 | `codex/root-child-inclusive/main.jsonl`, `child.jsonl` | `expected/codex-root-child-inclusive.json` | C-CODEX-006/007 | root `185`; child `40` 추가분 `0` |
| `codex-root-scope-unknown` | root scope 미확인 시 임의 합산 금지 | `codex/root-scope-unknown/main.jsonl`, `child.jsonl` | `expected/codex-root-scope-unknown.json` | C-CODEX-006/007 | 완전 합계 후보 `120` 또는 `160`; 결과 `null`, known root `120` |
| `codex-nested-child` | nested child의 immediate parent와 root session 분리 | `codex/nested-child.jsonl` | `expected/codex-nested-child.json` | C-CODEX-008/009 | `50+20+10=80`; root session은 record `session_id` 유지 |
| `codex-delta-snapshot-mismatch` | delta 합과 turn snapshot 불일치에서 snapshot authority | `codex/delta-snapshot-mismatch.jsonl` | `expected/codex-delta-snapshot-mismatch.json` | C-CODEX-003/004 | delta `100`, final snapshot `120`; 결과 `120` partial |
| `codex-usage-edge-cases` | total-only, nonzero cache write, missing ID, 포함관계·total 위반 | `codex/usage-edge-cases.jsonl` | `expected/codex-usage-edge-cases.json` | C-CODEX-008/010/011/012/013 | total-only는 native `77`; cache write는 `100+30=130`; 세 위반은 invalid |
| `codex-fork-replay` | origin evidence 있는 replay dedupe와 evidence 없는 ambiguous 분리 | `codex/fork-replay/parent.jsonl`, `fork-with-origin.jsonl`, `fork-without-origin.jsonl` | `expected/codex-fork-replay.json` | C-CODEX-017/018 | 같은 origin replay `0`, 새 실행 `10`; 무증거 `30`은 미합산·미제거 |
| `codex-interrupt-late-child` | main Interrupt, child Interrupt 부재, 늦은 SubagentStop | `codex/interrupt-late-child/revision-1-main.jsonl`, `revision-2-child.jsonl`, `hooks.json` | `expected/codex-interrupt-late-child.json` | C-CODEX-014/015 | rev1 `60`; rev2 `60+20=80`, root key 유지 |
| `codex-hook-stdout` | Stop/SubagentStop `{}`, Interrupt 빈 stdout, plain/block/context 금지 | `codex/hook-stdout.json` | `expected/codex-hook-stdout.json` | C-CODEX-014/015/016 | 이벤트별 exact neutral bytes 비교; token 산식 없음 |

## 미확인 의미의 표현

- Claude Hook `prompt_id == transcript.promptId`는 fixture에서도 직접 동일성 계약으로 승격하지 않는다. Hook은 session rescan trigger이고 transcript `promptId`가 Turn authority다.
- Claude fork parent를 Hook으로 복원할 수 없으므로 `fork_parent_unknown`과 별도 session을 유지한다. 동일 entry UUID가 있는 복사만 제외한다.
- Claude `thinking_tokens`는 native에 남기고 normalized `reasoning`은 `null`이다.
- task notification에 identity가 없을 때 새 Turn을 만들지 않고 pending child가 끝날 때까지 `provisional`이다.
- Codex total-only와 nonzero cache write는 native를 보존하지만 각각 `total_only_semantics_unknown`, `cache_write_semantics_unknown`을 낸다. cache write `7`은 processed `130`에 더하지 않는다.
- `rootScope=unknown`은 root와 child를 모두 보존하면서 완전 normalized 합계를 `null`로 둔다.
- fork replay에 origin evidence가 없으면 dedupe도 합산도 하지 않고 `fork_replay_ambiguous`로 남긴다.
- Stop 또는 Interrupt는 usage 완결성과 별개의 lifecycle evidence다. 늦은 child나 tail이 있으면 같은 root key의 revision을 교체한다.

## 검증

검증 시 다음을 확인한다.

1. 모든 `.json` 파일을 JSON parser로 읽고, 모든 `.jsonl` 파일의 비어 있지 않은 각 줄을 독립 JSON으로 읽는다.
2. 각 expected의 `inputFiles`가 존재하고, `claude/`와 `codex/` 아래 모든 입력 파일이 정확히 한 expected에 매핑되는지 확인한다.
3. expected마다 `providerVersion`, `recordShape`, `synthetic=true`, 비어 있지 않은 `claimIds`, native/normalized usage, membership, quality, diagnostics 또는 해당 계약에서 명시적인 `null`을 확인한다.
4. `content`, `prompt`, `tool_input`, `tool_result` 본문 키와 실제 사용자 경로/ID/secret 패턴을 검색한다.
5. 수치는 구현 parser 결과가 아니라 이 문서와 expected의 `manualCalculation`으로 다시 계산한다.
