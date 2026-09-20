# 성능 검증

측정일은 2026-09-20이다. 모든 입력은 임시 디렉터리에 생성한 합성 JSONL이며 원문 body는 이 문서나 패키지에 포함하지 않았다.

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

공식 fixture는 정확히 22,020,096 bytes(21 MiB), 100,000행이다. 25,000행은 Codex 0.153.4의 delta/turn/session usage object를 모두 가진 지원 record이고, 75,000행은 JSON 파싱 뒤 무시되는 합성 event record다. 매 실행에서 전체 파일을 line 단위로 파싱하며 `apiCalls=25,000`, `processedTokens=25,000`을 확인한다. fixture SHA-256은 `0833D2297E4352AEF5614F831A68A92461C7843B4BAAFCF642A52E04EFFDA8CD`다.

재현 명령:

```powershell
dotnet restore packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj --locked-mode
dotnet build packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj -c Release --no-restore
dotnet run --project packaging/benchmarks/TaskTokenMeter.PerformanceProbe.csproj -c Release --no-build -- "<absolute-path-to-task-token-meter.exe>"
```

## 공식 결과

| 지표 | cold | warm p50 | warm p95 | warm max | 목표 | 판정 |
|---|---:|---:|---:|---:|---:|---|
| elapsed | 1,606.82 ms | 1,586.26 ms | 1,648.19 ms | 1,683.55 ms | p95 ≤ 1,000 ms | **Fail** |
| peak RSS | 121.28 MiB | 139.21 MiB | 144.25 MiB | 144.43 MiB | max ≤ 256 MiB | Pass |

elapsed 실패 원인은 현재 Codex adapter가 100,000행을 매번 처음부터 JSON 파싱하고, 지원 usage record마다 raw record와 delta/turn/session 관측값을 materialize한 뒤 lineage·dedupe·projection을 수행하는 구조에 있다. 후속 작업은 streaming projection 또는 안전한 incremental checkpoint를 도입하고, truncate/tail/parser-version fallback을 함께 검증하는 것이다. 이 최적화 전에는 성능 수용 기준을 통과로 처리하지 않는다.

**PERF-001 후속 작업:** Codex adapter가 raw record와 세 종류 observation 전체를 동시에 보관하지 않도록 streaming projection 또는 검증된 incremental checkpoint를 구현한다. truncate, partial tail, parser version 변경 시 full-rescan fallback과 기존 dedupe/lineage 결과 동일성을 테스트하고, 이 문서의 21 MiB fixture로 cold 1회와 warm 30회를 다시 측정해 p95 ≤ 1,000 ms 및 peak RSS ≤ 256 MiB를 모두 만족해야 완료한다.

ReadyToRun self-contained package를 같은 공식 fixture로 cold 1회와 warm 1회 비교했다. 각각 1,627.93 ms와 1,631.11 ms였고 1초에 근접하지 않아 공식 30회 측정을 반복하지 않았다. 공식 판정은 위 30회 결과다.

69,155,580-byte stress fixture(100,000개의 완전한 usage record)도 별도로 측정했다. cold 3,395.26 ms, warm 30회 p50 3,410.21 ms, p95 3,504.98 ms, max 3,548.89 ms였고 peak RSS max는 225.25 MiB였다. 이 수치는 20 MiB 수용 기준 판정에 사용하지 않는다.

## Hook 수락 및 process 시작

`HookEntryPoint`가 검증된 Codex payload를 받아 실제 Windows `rundll32.exe` stub process를 시작하는 경로를 30회 측정했다. neutral stdout `{}`와 종료 코드 0을 매번 확인했다.

| 지표 | p50 | p95 | max | 목표 | 판정 |
|---|---:|---:|---:|---:|---|
| acceptance + process start | 7.34 ms | 9.87 ms | 17.40 ms | p95 ≤ 250 ms | Pass |

timeout, process-start failure, malformed payload는 `HookTests.WorkerTimeoutAndRouteConflictAreAbsorbed`와 `HookTests.ParseStartMissingExecutableAndCancellationFailuresAreAlwaysNeutral`에서 비차단 종료를 검증한다.
