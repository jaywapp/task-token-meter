# Provider Hook 설치와 복구

Task Token Meter Hook은 Provider 작업을 막지 않는 재집계 트리거다. Hook 자체는 usage를 계산하거나 Turn을 만들지 않고, 검증된 session·turn·workspace·transcript 경로만 비동기 worker에 전달한다. worker는 기존 Adapter와 활성 storage registry route를 사용한다. 저장 방식 migration과 겹쳐 route generation이 달라지면 최신 route를 다시 읽어 한 번 재시도한다.

대화 본문, 마지막 assistant message, prompt, tool 인자, 환경 변수 값은 전달·저장·출력하지 않는다. 모델 호출과 context 주입도 하지 않는다.

## 지원 범위

| Provider | 검증 버전 | 설치 이벤트 | 성공 stdout |
|---|---|---|---|
| Claude Code | `2.1.278` | `Stop`, `SubagentStop`, `StopFailure` | 빈 출력 |
| Codex CLI | `0.153.4` | `Stop`, `SubagentStop`, `Interrupt` | Stop 계열은 `{}`, Interrupt는 빈 출력 |

다른 버전은 설치하지 않는다. Provider 설정 형식은 별도 구현이며 Claude 설정을 Codex에 복사하거나 그 반대로 사용하지 않는다. Codex Hook은 조사 당시 공식 계약과 합성 fixture로 고정했으며 실제 사용자 설치 smoke는 배포 검증 단계에서 별도로 수행해야 한다.

## 명령

설치는 사용자가 명시적으로 실행할 때만 설정 파일을 바꾼다. 실행 파일은 존재하는 절대 경로여야 한다. Provider 버전도 명시해야 한다.

```powershell
token-meter hook install --provider claude --provider-version 2.1.278 --executable C:\Tools\TaskTokenMeter\task-token-meter.exe
token-meter hook install --provider codex --provider-version 0.153.4 --executable C:\Tools\TaskTokenMeter\task-token-meter.exe

token-meter hook status --provider claude --json
token-meter hook status --provider codex --json

token-meter hook uninstall --provider claude
token-meter hook uninstall --provider codex
```

테스트나 별도 설정 위치에서는 `--settings <절대 JSON 경로>`를 사용한다. 기본 경로는 Claude `~/.claude/settings.json`, Codex `~/.codex/hooks.json`이다.

installer는 기존 JSON을 구조적으로 읽고 unknown field와 unrelated Hook을 유지한다. Task Token Meter가 추가한 `--managed-by task-token-meter-v1` command 항목만 제거한다. 같은 설치·제거를 반복해도 결과가 바뀌지 않는다.

## 원자 write와 복구

설정을 바꾸기 전에 같은 디렉터리에 `<settings>.task-token-meter.bak`을 만들고, UTF-8 임시 파일을 같은 디렉터리에서 원자 교체한다. JSON 손상, 읽기 전용 파일, 권한 오류, 임시 write 실패에서는 현재 설정을 변경하지 않는다. 교체 과정에서 대상 파일이 사라진 예외 상황에는 backup을 즉시 복원한다.

수동 복구가 필요하면 Provider를 종료한 뒤 backup이 정상 JSON인지 확인하고 원래 설정 경로로 복사한다. Hook 명령의 `status`는 backup 존재 경로를 JSON의 `backupPath`로 보여준다.

## 비차단 처리

Hook entrypoint는 stdin을 최대 64 KiB까지만 읽고 지원 이벤트, ID 길이, 절대 workspace, `.jsonl` transcript와 Provider source allowlist를 검증한다. 결과와 관계없이 성공 코드 `0`과 Provider별 neutral stdout을 반환한다. process 시작 실패, malformed payload, 취소, timeout, Adapter·storage·parse 오류는 사용자 작업으로 전파하지 않는다.

실제 집계는 별도 `hook worker` process가 수행한다. worker는 Hook process의 표준 스트림을 상속하지 않는다(세 스트림을 리다이렉트한 뒤 곧바로 닫는다). 따라서 Hook process가 끝나는 즉시 Provider의 stdout pipe가 닫히고, Provider는 aggregation 완료를 기다리지 않는다. 기본 제한 시간은 10초다. 실패 진단은 `%LOCALAPPDATA%/TaskTokenMeter/diagnostics/hooks.jsonl`에 code, provider, event, duration만 기록하며 10 MiB에서 회전한다. session ID, 경로, 예외 메시지, 원문 context는 기록하지 않는다.

`packaging/hooks/claude`와 `packaging/hooks/codex`에는 배포 manifest와 Windows wrapper가 따로 있다. wrapper는 패키지 안의 실행 파일이 사라져도 성공 종료하며, Codex wrapper는 `{}` neutral JSON을 반환한다.
