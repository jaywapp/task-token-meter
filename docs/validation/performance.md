# 성능 검증

CLI 조회 측정일은 `PERF-001` 최적화 후인 2026-09-21이고, 아래 Hook 측정일은 2026-09-20이다. 모든 입력은 임시 디렉터리에 생성한 합성 JSONL이며 원문 body는 이 문서나 패키지에 포함하지 않았다.

## 환경과 방법

| 항목 | 값 |
|---|---|
| OS | Microsoft Windows NT 10.0.26200.0, win-x64 |
| CPU | AMD Ryzen 7 7800X3D, 8 cores / 16 logical processors |
| SDK / runtime | .NET SDK 10.0.400 / .NET 10.0.11 |
| probe | `packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj` Release |
| 대상 | Release framework-dependent `task-token-meter.exe` |
| RSS | 대상 process working set을 5 ms 간격으로 표본화한 최댓값 |
| cold | fixture 생성 뒤 첫 별도 process. OS file cache 강제 flush는 하지 않음 |
| warm | 같은 fixture를 읽는 새 process 30회. 각 실행은 JSON 결과도 검증 |

공식 fixture는 정확히 22,020,096 bytes(21 MiB), 100,000행이다. 25,000행은 Codex 0.153.4의 delta/turn/session usage object를 모두 가진 지원 record이고, 75,000행은 지원 record가 아니어서 무시되는 합성 event record다. 매 실행에서 전체 파일을 line 단위로 파싱하며 `apiCalls=25,000`, `processedTokens=25,000`을 확인한다. fixture SHA-256은 `0833D2297E4352AEF5614F831A68A92461C7843B4BAAFCF642A52E04EFFDA8CD`다.

재현 명령:

```powershell
dotnet restore packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj --locked-mode
dotnet build packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj -c Release --no-restore
dotnet run --project packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj -c Release --no-build -- "<absolute-path-to-task-token-meter.exe>"
```

## 공식 결과

| 지표 | cold | warm p50 | warm p95 | warm max | 목표 | 판정 |
|---|---:|---:|---:|---:|---:|---|
| elapsed | 724.29 ms | 764.83 ms | 821.47 ms | 823.23 ms | p95 ≤ 1,000 ms | **Pass** |
| peak RSS | 83.67 MiB | 87.82 MiB | 88.65 MiB | 89.02 MiB | max ≤ 256 MiB | Pass |

**PERF-001 판정: 해소.** warm p95는 목표 대비 178.53 ms(17.9%) 여유가 있고 peak RSS max는 한도의 34.8%다. 같은 machine에서 같은 fixture로 최적화 직전 baseline을 다시 측정한 값(아래 "이전 측정")과 비교하면 warm p95가 1,662.57 ms에서 821.47 ms로 2.02배 빨라졌고 peak RSS max는 122.55 MiB에서 89.02 MiB로 줄었다.

### 무엇을 바꿨나

Codex adapter의 읽기 경로를 streaming projection으로 바꿨다. 관측 결과, dedupe·lineage·projection 산술이 아니라 **행마다 JSON 문서를 만드는 비용**이 지배적이었다.

- `File.ReadLines` + 행별 `JsonDocument.Parse`를 UTF-8 byte 단위 행 reader와 `Utf8JsonReader` 직접 파싱으로 교체했다. 이전에는 모든 byte를 UTF-16 string으로 디코딩한 뒤 JSON parser가 다시 UTF-8로 되돌렸고, 행마다 document database를 만들었다. 이제 원본 byte를 그대로 읽는다.
- 지원하지 않는 record는 payload를 materialize하지 않는다. 공식 fixture의 75,000개 무시 record는 식별에 필요한 최소 token만 읽고 지나간다. record type 판별도 string을 만들지 않고 UTF-8 리터럴과 직접 비교한다.
- 지원 record의 관측값을 두 번 만들던 구조를 없앴다. 이전에는 delta/turn/session observation을 읽는 중에 한 번 만들고, lineage 보강 뒤 전부 버리고 다시 만들었다.
- hot loop의 LINQ 할당을 제거했다. execution grouping, usage 검증, metric 합산이 record마다 배열·리스트·delegate를 만들지 않는다.

행 분리 규칙(LF/CR/CRLF), byte order mark, 중복 property의 "첫 값 우선", 잘못된 JSON의 `malformed_json_line` 진단, 비-object 행의 실패 방식은 모두 이전과 같다. incremental checkpoint는 도입하지 않았으므로 truncate/tail/parser-version fallback 경로도 새로 생기지 않았다. 집계 결과, dedupe·lineage 의미, JSON/text 출력 형태, 종료 코드는 바뀌지 않았고 `tests/fixtures/expected/`의 수동 oracle도 그대로다.

### 남은 여지

CLI `current` 한 번은 아직 source를 세 번 읽는다. `SessionSelector`가 후보 조회와 선택 후 재검증으로 두 번, `ReadTurnsAsync`가 한 번 `AdapterCliRuntime.ReadAll`을 호출하기 때문이다. process 시작 비용이 약 60 ms이므로 위 821.47 ms 중 약 250 ms가 1회 parse 비용이다. 이 중복을 없애려면 파일 상태에 기반한 read cache가 필요한데, 이는 [architecture.md](../prepare/architecture.md)가 incremental 파싱에 요구하는 file identity·truncate 감지·full-rescan fallback 검증을 함께 갖춰야 한다. 현재 목표를 여유 있게 만족하므로 이번 변경에서는 도입하지 않았다.

### 이전 측정 (최적화 전)

같은 machine, 같은 fixture(SHA-256 동일), 같은 probe로 측정한 최적화 직전 baseline이다.

| 지표 | cold | warm p50 | warm p95 | warm max | 판정 |
|---|---:|---:|---:|---:|---|
| elapsed | 1,705.95 ms | 1,611.50 ms | 1,662.57 ms | 1,721.50 ms | Fail |
| peak RSS | 118.98 MiB | 118.60 MiB | 122.36 MiB | 122.55 MiB | Pass |

`PERF-001`을 처음 기록한 2026-09-20 측정치는 elapsed cold 1,606.82 ms, warm p50 1,586.26 ms, p95 1,648.19 ms, max 1,683.55 ms, peak RSS cold 121.28 MiB / p50 139.21 MiB / p95 144.25 MiB / max 144.43 MiB였다. 위 재측정과 같은 구간이며, 당시 지목한 실패 원인(행마다 전체 파일을 다시 파싱하고 raw record와 세 종류 관측값을 함께 materialize하는 구조)도 이번 최적화에서 그대로 제거했다.

아래 두 보조 측정은 최적화 **전** 수치이며 이번에 다시 측정하지 않았다. 공식 판정에는 쓰지 않는다.

- ReadyToRun self-contained package를 같은 공식 fixture로 cold 1회와 warm 1회 비교했을 때 각각 1,627.93 ms와 1,631.11 ms였다. 당시 1초에 근접하지 않아 공식 30회 측정을 반복하지 않았다.
- 69,155,580-byte stress fixture(100,000개의 완전한 usage record)는 cold 3,395.26 ms, warm 30회 p50 3,410.21 ms, p95 3,504.98 ms, max 3,548.89 ms, peak RSS max 225.25 MiB였다. 이 수치는 20 MiB 수용 기준 판정에 사용하지 않는다.

### SCOPE-001 이후 재확인 (2026-09-22)

SCOPE-001(자동 discovery의 workspace 필터)이 root Turn 하나당 canonical workspace를 한 번 계산하도록 `CodexUsageAdapter.BuildRootTurn`과 Claude `BuildTurns`를 바꿨다. 이 계산은 record 하나마다가 아니라 Turn 하나마다(공식 fixture는 25,000개 record가 소수의 Turn으로 묶인다) 실행되고, 공식 fixture는 `cwd`/`turn_context`/`session_meta.cwd`를 채우지 않으므로 `WorkspaceRoot.TryFindGitRoot`가 즉시 null을 반환해 파일시스템 I/O가 전혀 발생하지 않는다. 같은 공식 fixture로 같은 probe를 다시 실행해 회귀가 없음을 확인했다: cold 1,059.74 ms, warm p50 732.80 ms, p95 764.97 ms, max 778.49 ms, peak RSS max 115.98 MiB. `passed: true`이며 위 공식 수치(warm p95 821.47 ms)와 같은 구간이다.

## Hook 수락 및 process 시작

`HookEntryPoint`가 검증된 Codex payload를 받아 실제 Windows stub process(`%ComSpec%`, 즉 `cmd.exe`)를 시작하는 경로를 30회 측정했다. neutral stdout `{}`와 종료 코드 0을 매번 확인했다. launcher가 stdin을 곧바로 닫으므로 `/c` 없이 시작된 `cmd.exe`는 EOF를 읽고 즉시 종료한다.

| 지표 | p50 | p95 | max | 목표 | 판정 |
|---|---:|---:|---:|---:|---|
| acceptance + process start (2026-09-21) | 3.16 ms | 8.89 ms | 11.79 ms | p95 ≤ 250 ms | Pass |

같은 실행의 CLI 수치는 cold 748.11 ms, warm p50 702.04 ms, p95 730.53 ms, max 731.41 ms, peak RSS max 108.91 MiB로 `PERF-001` 판정도 재현했다. 실행 전후 `cmd.exe`와 `rundll32.exe` 프로세스 수가 같아 stub이 남지 않음을 확인했다.

### 이전 stub과 발견한 결함 (2026-09-21)

이전 probe는 `rundll32.exe`를 stub으로 썼다. `rundll32.exe`는 worker 인자를 DLL 이름으로 해석해 오류 대화상자를 띄운 채 종료하지 않아, probe를 실행할 때마다 프로세스가 누적됐다(한 작업 세션에서 159개). 2026-09-20 기록(p50 7.34 ms, p95 9.87 ms, max 17.40 ms)은 이 stub으로 측정한 값이다.

원인을 추적하다 제품 결함도 발견했다. `ProcessHookWorkerLauncher`가 stdin만 리다이렉트해 worker가 Hook process의 stdout/stderr를 상속했다. 이 경우 Provider의 stdout pipe가 worker 종료 시점까지 열려 있어 Provider가 neutral 응답 대신 aggregation 완료를 기다리게 되고, worker 출력이 Hook 응답에 섞일 수 있다. probe가 반환되지 않고 멈춘 것, `cmd.exe` stub의 배너가 probe JSON에 섞인 것이 같은 증상이다. launcher는 이제 세 표준 스트림을 모두 리다이렉트하고 곧바로 닫으며, 위 측정에서 probe 출력에 stub 배너가 섞이지 않았다.

timeout, process-start failure, malformed payload는 `HookTests.WorkerTimeoutAndRouteConflictAreAbsorbed`와 `HookTests.ParseStartMissingExecutableAndCancellationFailuresAreAlwaysNeutral`에서 비차단 종료를 검증한다.
