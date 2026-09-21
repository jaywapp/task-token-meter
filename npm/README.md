# npm 패키지

`packaging/Build-NpmPackages.ps1`이 GitHub Release 패키지로 두 개의 npm 패키지를 만든다.

| 디렉터리 | 패키지 | 내용 |
|---|---|---|
| `task-token-meter` | `task-token-meter` | Node launcher, `bin` 등록, 플랫폼 선택 |
| `platform-win32-x64` | `@jaywapp/task-token-meter-win32-x64` | Release에서 검증한 self-contained 실행 파일 |

설치 시점에 바이너리를 내려받지 않는다. lifecycle script 비활성화, proxy, offline cache 같은 환경 차이로
설치 성공 여부가 달라지지 않도록 플랫폼 패키지가 검증된 파일을 직접 포함한다.

이 디렉터리의 `package.json` 버전은 `0.0.0-dev` placeholder이며, 빌드 스크립트가 release 버전으로 바꾼다.
`dist/`는 빌드 산출물이므로 저장소에 커밋하지 않는다.
