# 저장소와 Ledger

Task Token Meter는 로컬 SQLite ledger를 사용한다. 전역 모드와 workspace 모드는 같은 schema와 transaction 계약을 사용하며, workspaceId는 저장 모드와 무관하다. 같은 workspace를 다른 모드로 전환해도 logical identity는 바뀌지 않는다.

## 저장 위치

| 모드 | 기본 경로 | 범위 |
|---|---|---|
| global | %LOCALAPPDATA%/TaskTokenMeter/ledger.db | 여러 workspace를 workspaceId로 분리 |
| workspace | &lt;workspace&gt;/.token-meter/ledger.db | 해당 root workspace |

SqliteLedgerStore.FromRoute는 StorageRoute.CanonicalDataRoot 아래의 ledger.db를 연다. UNC와 Windows network drive는 거부한다. WAL의 network filesystem 제약을 우회하거나 자동 fallback하지 않는다. 테스트는 매번 별도 임시 디렉터리를 사용하며 실제 사용자 경로나 DB를 열지 않는다.

## 활성 route 계약

StorageRoute에는 다음 값이 모두 포함된다.

- 저장 모드와 독립된 WorkspaceId
- Global 또는 Workspace 모드
- canonical data root
- 단조 증가하는 RouteGeneration
- 활성 저장소를 구별하는 ActiveStoreId

각 DB의 workspace_routes 행은 workspace별 route를 하나만 허용한다. commit과 purge는 mode, root, generation, store ID를 transaction 안에서 다시 비교한다. BEGIN IMMEDIATE가 단일 활성 writer를 만들며, 긴 source 읽기는 이 transaction 밖에서 끝내야 한다.

IStorageRouteAuthority는 두 DB 사이의 활성 route를 검증하는 공통 계약이다. route registry와 실제 global ↔ workspace migration, backup, journal, registry atomic replace는 TASK-015가 이 계약을 구현해 연결한다. authority가 연결된 store는 DB transaction을 열기 전에도 현재 route를 확인한다. 전환 뒤의 오래된 writer는 RouteConflict가 되고 다른 저장소로 fallback하지 않는다.

## 저장 모드 전환과 복구

FileStorageRouteRegistry는 중앙 config를 route authority로 사용한다. canonical workspace root에서 deterministic workspaceId를 만들고, workspace lock을 먼저 획득한 뒤 registry lock 안에서 revision CAS와 atomic replace를 수행한다. 갱신할 workspace 항목만 교체하므로 동시에 추가·변경된 다른 workspace 항목은 병합해 보존한다.

StorageMigrationService는 status, dry-run, migrate 흐름을 제공한다. SQLite online backup으로 출발 저장소의 일관된 복사본을 만들고, 대상에는 해당 workspace의 행만 import한다. projection, root/child membership, native usage, quality, source manifest를 목적지에서 다시 검증한 다음 route를 전환한다. 독립적으로 생성된 목적지 데이터가 있으면 충돌로 중단한다.

migration journal에는 migration ID, source/destination path, store ID, generation, manifest와 phase만 기록한다. commit 경계는 BackupCompleted → DestinationCommitted → RouteSwitched → Completed이며 각 단계는 재시도할 수 있다. route가 이미 바뀐 뒤 중단된 실행은 journal을 완료 상태로 수렴시킨다. 출발 DB와 backup은 자동 삭제하지 않고, superseded lineage receipt를 registry에 남겨 왕복 전환과 원본 로그 정리 뒤에도 검증된 계보를 유지한다.

workspace DB를 만들기 전에 local Git exclude에 /.token-meter/를 한 번만 추가한다. 기존 항목과 줄바꿈은 그대로 보존한다. 이미 tracked된 경로, exclude 갱신 실패, readonly 목적지, disk full 또는 journal/backup/import/route switch 실패가 발생하면 기존 활성 route를 유지하며 자동 fallback이나 source 삭제를 수행하지 않는다.

검증은 모두 별도 임시 workspace와 registry에서 수행하며 현재 repository의 .git/info/exclude나 실제 사용자 DB를 수정하지 않는다. 양방향 왕복, 동시 다른 workspace registry mutation, 모든 journal commit 경계의 crash/retry, 늦은 Hook writer, 독립 목적지 충돌, readonly/disk failure, Git exclude 보존·실패를 통합 테스트로 재현한다.
## Schema

현재 PRAGMA user_version은 2다.

| 버전 | 변경 |
|---|---|
| 1 | workspace_routes, turns, sources, unique key와 foreign key |
| 2 | 제한된 운영 진단을 위한 diagnostics와 시간 index |

모든 migration step과 user_version 갱신은 하나의 immediate transaction에 있다. 실패하면 이전 schema와 version을 유지한다. 지원 버전보다 새로운 DB는 열지 않는다.

주요 unique key는 다음과 같다.

- Turn: (workspace_id, provider, root_session_id, root_turn_id)
- Source: (workspace_id, source_id)
- Route: workspace_id

projection은 versioned Core DTO를 JSON으로 보존하고 SHA-256 hash를 함께 저장한다. 이 JSON에는 transcript 본문, prompt, tool input, 환경 변수 또는 credential을 넣지 않는다.

## Commit과 동시성

LedgerCommitRequest는 route, 새 TurnProjection, ExpectedRevision, source snapshot 목록을 받는다. 새 projection revision은 expected revision보다 정확히 1 커야 한다.

commit 순서는 다음과 같다.

1. 외부 route authority가 있으면 활성 generation과 store를 확인한다.
2. immediate transaction을 시작하고 DB의 route를 다시 확인한다.
3. source generation과 fingerprint/read extent를 비교한다.
4. 동일 projection hash이면 source checkpoint만 안전하게 갱신하고 Unchanged를 반환한다.
5. hash가 다르면 현재 Turn revision과 ExpectedRevision을 비교한다.
6. source와 Turn을 같은 transaction에서 쓰고 commit한다.

같은 snapshot을 여러 writer가 보내도 unique key와 hash 비교로 한 건만 저장된다. 더 높은 source generation이 먼저 commit되면 낮은 generation은 SourceConflict다. 서로 다른 projection이 같은 expected revision으로 경합하면 먼저 commit한 writer만 성공하고 나머지는 RevisionConflict다.

SQLite busy/locked 오류만 제한 시간 안에서 재시도한다. 기본값은 SQLite busy timeout 2초, 최대 재시도 시간 5초, retry delay 25ms다. ILedgerDelay와 ILedgerFaultInjector로 busy, transaction 중단, disk write 실패, migration 실패를 실제 대기나 사용자 DB 없이 재현할 수 있다.

## Source 손실과 복구

Available + IsComplete인 source만 기존 projection을 교체할 수 있다. 다음 입력은 보관된 정상값을 변경하지 않고 Preserved를 반환한다.

- source missing
- partial tail 또는 truncate
- 권한 거부 등 unreadable
- pending, unsupported 또는 incomplete

이 경우 최소 진단만 추가한다. source가 없는 rebuild도 저장된 토큰을 0이나 빈 projection으로 바꾸지 않는다. 이후 완전한 source가 더 높은 generation으로 들어오면 정상적인 revision commit으로 복구한다.

transaction의 source write 뒤, projection write 뒤, commit 직전 오류는 모두 rollback된다. commit 완료 뒤 호출자가 결과를 받지 못한 경우 같은 요청을 다시 보내면 projection hash로 Unchanged가 되어 안전하게 수렴한다.

## 진단 보관과 purge

ledger 값은 자동 삭제하지 않는다. PreviewPurgeAsync가 workspace별 Turn, source, diagnostic 수를 보여주며, PurgeAsync만 명시적으로 Turn/source를 삭제한다. purge도 활성 route를 검증한다.

진단은 원문이나 예외 문자열 대신 code, scope key, count, UTC 시각만 저장한다. 기본 제한은 14일 또는 추정 payload 총 10 MiB 중 먼저 도달하는 값이다. 오래된 항목과 크기 제한을 넘는 가장 오래된 항목은 write transaction에서 정리된다. ledger projection에는 이 회전 정책을 적용하지 않는다.

## 검증

다음 명령은 실제 사용자 DB를 사용하지 않는다.

~~~powershell
dotnet test tests/TaskTokenMeter.IntegrationTests/TaskTokenMeter.IntegrationTests.csproj -c Release --filter FullyQualifiedName~LedgerTests
dotnet test TaskTokenMeter.sln -c Release
dotnet restore TaskTokenMeter.sln --locked-mode
~~~

LedgerTests는 두 모드의 동시 8 writer, 같은 snapshot 10회, 오래된 source의 역순 완료, route 전환 뒤 stale writer, transaction 중단, 쓰기 실패, migration rollback, missing/truncated/unreadable 보존, 명시 purge, bounded busy retry를 검증한다.
