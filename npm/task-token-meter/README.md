# task-token-meter

Claude Code와 Codex CLI 세션의 토큰 사용량을 root Turn 단위로 집계하는 Windows CLI다.

```powershell
npx task-token-meter@next --help
npm install -g task-token-meter@next
```

이 패키지는 실행 파일을 설치 시점에 내려받지 않는다. GitHub Release에서 검증한 self-contained 실행 파일이
플랫폼 패키지 `@jaywapp/task-token-meter-win32-x64`에 그대로 들어 있고, 이 패키지는 그것을 실행하는 launcher다.

Windows x64만 지원한다. 사용법, Hook 설치, 저장 모드, 알려진 제한은
[저장소 README](https://github.com/jaywapp/task-token-meter#readme)를 참고한다.

MIT 라이선스.
