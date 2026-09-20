# CLI 사용법

`token-meter`는 로컬 Claude Code 및 Codex usage 기록의 관측값을 표시한다. 프롬프트, 대화 본문, 도구 입력, 환경 변수, 자격 증명은 출력하거나 저장하지 않는다.

```powershell
token-meter current --provider codex --session session-id
token-meter last --provider claude-code --session session-id
token-meter turns --provider codex --session session-id --json
token-meter sync --provider codex --session session-id
token-meter rebuild --provider codex --session session-id
token-meter storage status --workspace 'D:\작업 공간\demo'
token-meter storage migrate --workspace 'D:\작업 공간\demo' --to workspace --dry-run
```

`current`는 실행 중을 포함한 최신 root Turn, `last`는 최근 terminal Turn을 우선하며 terminal 상태가 없으면 provisional 경고와 함께 최신 Turn을 표시한다. `turns`는 시간순 목록을 표시한다. `sync`는 선택한 세션을 명시적으로 다시 읽고, `rebuild`는 보관값을 지우지 않고 현재 존재하는 source만 다시 확인한다.

`--json`에서는 stdout에 schemaVersion이 있는 JSON 한 개만 쓴다. 진단은 stderr에 쓴다. JSON, CI, stdin/stdout 리디렉션, `--non-interactive`에서는 `--provider`와 `--session`이 필수이며 누락하면 입력을 기다리지 않고 종료 코드 4와 `selector_required` JSON 오류를 반환한다. 대화형에서는 후보가 여러 개일 때 번호를 입력하고 `q`, Ctrl+C, EOF는 130으로 취소한다.

종료 코드는 정상 완료 0, 내부 또는 I/O 오류 1, 잘못된 인수 2, 데이터 없음 3, selector 누락 또는 모호한 선택 4, 지원하지 않는 schema 5, `--strict` 측정 실패 6, 저장소 오류 7, 취소 130이다. `NO_COLOR` 또는 리디렉션 환경에서는 색을 쓰지 않는다.

기본 workspace는 현재 디렉터리에서 가장 가까운 Git root이며, Git root가 없으면 현재 디렉터리다. 기본 source root는 존재하는 경우에만 사용자 프로필의 `.claude/projects`와 `.codex/sessions`를 읽는다. `TOKEN_METER_CLAUDE_SOURCES`와 `TOKEN_METER_CODEX_SOURCES`는 이 기본값을 대체하는 명시적 override이며, 지원하지 않는 schema는 종료 코드 5로 구분한다. 저장 모드는 기본 global이며, workspace mode로 전환할 때 `storage migrate --to workspace`를 명시적으로 실행해야 한다. 전환은 미리보기와 검증 후 수행하며 자동 fallback 또는 두 저장소 동시 쓰기를 하지 않는다.