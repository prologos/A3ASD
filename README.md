# A3ASD

A3 게임 서버군이 쓰는 **ASD 미들웨어(H3DB 레코드 중계 서버)를 C#(.NET 10)으로 다시 만든 호환 서버**다.
게임 서버는 그대로 두고 원래 ASD 자리에 A3ASD 를 넣으면 같은 프로토콜로 동작한다.

A **drop-in replacement for the ASD middleware (H3DB record relay server)** used by the A3 game server suite, rewritten in C# (.NET 10).
Leave the game servers as they are, put A3ASD where the original ASD was, and it speaks the same protocol.

## 무엇을 하는 프로그램인가 / What it does

A3 서버군에서 LoginServer 와 MainServer 는 DB 에 직접 붙지 않고, ASD 라는 중계 서버에 텍스트 명령(`2021 <id>`, `2071 <id> …` 등)을 보내 계정·캐릭터·창고 레코드를 읽고 쓴다.
원래 ASD 는 Delphi + **BDE(Borland Database Engine)** 로 만들어져 있어 다음 문제가 있었다.

In the A3 suite, LoginServer and MainServer do not talk to the database directly. They send text commands (`2021 <id>`, `2071 <id> …`, etc.) to a relay server called ASD, which reads and writes account, character and storage records.
The original ASD is built on Delphi + **BDE (Borland Database Engine)**, which causes these problems:

- BDE 는 오래전에 지원이 끝난 32비트 데이터 엔진이라 최신 Windows 에 설치·유지하기 어렵다.
  BDE is a long-discontinued 32-bit data engine and is hard to install and maintain on current Windows.
- DB 연결이 BDE 별칭과 ODBC DSN 설정에 묶여 있다.
  Database access is tied to BDE aliases and ODBC DSN setup.

A3ASD 는 같은 일을 하되 **BDE 없이 `Microsoft.Data.SqlClient` 로 SQL Server 에 네이티브로 직접 연결**한다.
A3ASD does the same job but **connects natively to SQL Server through `Microsoft.Data.SqlClient`, with no BDE**.

- 게임 서버(LoginServer·MainServer)는 바꾸지 않는다. 게임 서버가 쓰는 요청의 와이어 형식·응답 문구·오류 코드를 원래 ASD 와 바이트 단위로 맞췄다.
  The game servers (LoginServer, MainServer) are not modified. For the requests they use, the wire format, reply text and error codes match the original ASD byte for byte.
- BDE·ODBC DSN 설정 없이 `H3DB.ini` 하나로 접속 정보를 지정한다. 연결 풀링·비동기 I/O 를 쓴다.
  Connection settings live in a single `H3DB.ini`; no BDE or ODBC DSN setup. Uses connection pooling and async I/O.
- 포트 하나가 테이블 하나에 대응한다(기본: `account` 7770, `charac0` 8880, `ItemStorage0` 9990). 한 프로세스가 모든 포트를 연다.
  One port maps to one table (defaults: `account` 7770, `charac0` 8880, `ItemStorage0` 9990). A single process opens all ports.
- LoginServer 의 **계정 만료 확인(Pay) 서버** 역할도 한다(`[pay]`, 기본 꺼짐).
  It can also act as LoginServer's **account expiry check (Pay) server** (`[pay]`, off by default).
- WinForms 로그 창 하나로 실행되며, 포트별 접속 상태와 DB 상태(`DB OK` / `DB FAIL`)를 보여 주고 일자별 로그 파일을 남긴다.
  Runs as a single WinForms log window showing per-port connections and DB state (`DB OK` / `DB FAIL`), and writes a daily log file.

```
LoginServer ──(2021)──────────► A3ASD :7770 ─┐
MainServer  ──(2011/2002/2071)► A3ASD :8880 ─┼─ SqlClient ─► SQL Server
            ──(2011/2002/2071)► A3ASD :9990 ─┘
LoginServer ──(Pay expiry)────► A3ASD :3000 (optional)
```

## 요구 사항 / Requirements

- Windows (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (`Microsoft.WindowsDesktop.App` 10.x) — 실행용 / to run
- .NET 10 SDK — 빌드용 / to build
- SQL Server. 게임 DB 에 `account`, `charac0`, `ItemStorage0` 테이블이 있어야 한다(A3ASD 는 스키마를 만들거나 바꾸지 않는다).
  SQL Server with the game tables `account`, `charac0` and `ItemStorage0` (A3ASD never creates or alters the schema).

## 빌드 / Build

```
dotnet build src -c Release
```

결과 / Output: `src\bin\Release\net10.0-windows\A3ASD.exe`

배포용 단일 exe(프레임워크 종속, 약 6MB) / Single-file deployment exe (framework-dependent, about 6 MB):

```
dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

`publish\A3ASD.exe` 와 `publish\H3DB.ini` 두 파일을 서버에 복사하면 된다.
Copy the two files `publish\A3ASD.exe` and `publish\H3DB.ini` to the server.

## 설정 / Configuration (`H3DB.ini`)

exe 와 같은 폴더의 `H3DB.ini` 를 읽는다. 다른 파일을 쓰려면 `A3ASD.exe <경로>`(상대경로는 exe 폴더 기준).
파일 인코딩은 시스템 ANSI(한국어 Windows = CP949, UTF-8 BOM 도 허용), 줄 끝은 CRLF 를 권장한다.

`H3DB.ini` is read from the exe folder. To use another file run `A3ASD.exe <path>` (relative paths resolve against the exe folder).
The file is in the system ANSI code page (CP949 on Korean Windows; a UTF-8 BOM is also accepted). CRLF line endings are recommended.

```ini
[db]
mode=mssql                 ; mssql | memory (DB 없이 자체 테스트 / self-test without a DB)
server=127.0.0.1           ; SQL Server 주소 / address (e.g. 10.0.0.5,1433 or HOST\SQLEXPRESS)
database=A3
user=sa                    ; 비우면 Windows 인증 / empty = Windows authentication
password=CHANGE_ME
extra=TrustServerCertificate=True;Encrypt=False;   ; 추가 연결 문자열 / extra connection string

[socket]
; NN=<port> <table> [role] [client]
00=7770 account Account LoginServer
01=8880 charac0 CharDB Main
02=9990 ItemStorage0 StorageDB Main

[pay]
port=0                     ; 계정 만료 확인, 0 = 끔 / expiry check, 0 = off (LoginServer default port 3000)
table=account
column=d_edate

[log]
wire=1                     ; 요청/응답 한 줄 요약 / one-line summary per message
payload=0                  ; 1 = 본문 전체(대용량·민감, 디버그용) / full payload (large, sensitive, debug only)
dir=log                    ; exe 폴더 기준 / relative to the exe folder
keepdays=30                ; 로그 보관 일수, 0 = 삭제 안 함 / days to keep logs, 0 = forever
```

비밀번호는 서버의 ini 에만 넣는다. 저장소에 올리지 않는다.
Put the real password only in the ini on the server. Never commit it.

## 사용법 / Usage

1. 원래 ASD 를 끈다(같은 포트를 쓰므로 동시에 띄울 수 없다).
   Stop the original ASD (both use the same ports, so they cannot run together).
2. `H3DB.ini` 의 `[db]` 를 실제 DB 에 맞추고, `[socket]` 포트를 원래 ASD 의 포트와 같게 둔다.
   Set `[db]` in `H3DB.ini` to the real database and keep the `[socket]` ports the same as the original ASD.
3. `A3ASD.exe` 를 실행한다. 창이 열리면 자동으로 시작하고, 하단 상태 줄에 `DB OK` 와 포트별 `ON 현재/누적 접속` 이 보인다.
   Run `A3ASD.exe`. It starts automatically when the window opens; the status bar shows `DB OK` and `ON active/total` per port.
4. 게임 서버는 설정을 바꿀 필요가 없다. 주소·포트가 원래 ASD 와 같으면 LoginServer 는 5초 안에, MainServer 는 재접속 주기에 맞춰 자동으로 붙는다.
   The game servers need no changes. With the same address and ports, LoginServer reconnects within 5 seconds and MainServer on its own reconnect cycle.
   - LoginServer: `LoginServer.ini` `[LOGINASD]` `COUNT` / `IP0` / `PORT0` → [docs/LoginServer-ASD.md](docs/LoginServer-ASD.md)
   - MainServer: `SvrInfo.ini` `[SvrInfo]` `CharacDB*` / `StorageDB*` → [docs/MainServer-ASD.md](docs/MainServer-ASD.md)
   - 만료 확인을 쓰려면 `[pay] port=3000` 과 `account.d_edate` 컬럼 / For expiry checks: `[pay] port=3000` and an `account.d_edate` column → [docs/LoginServer-Pay.md](docs/LoginServer-Pay.md)
5. 창을 닫으면 모든 포트를 닫고 종료한다. 게임 서버는 A3ASD 가 다시 뜨면 스스로 다시 붙는다.
   Closing the window closes all ports and exits. The game servers reconnect on their own when A3ASD comes back.

### DB 준비(권장) / Database preparation (recommended)

모든 요청이 `WHERE c_id = @id` 로 조회하므로 `c_id` 인덱스가 없으면 요청마다 테이블 스캔이 된다.
Every request filters on `WHERE c_id = @id`; without an index on `c_id` each request is a table scan.

- `sql/index_cid.sql` — 세 테이블의 `c_id` 인덱스 점검·생성(여러 번 실행해도 안전) / checks and creates `c_id` indexes on the three tables (safe to re-run)
- `sql/add_d_edate.sql` — Pay 만료 확인용 `account.d_edate` 컬럼 추가 / adds the `account.d_edate` column for the Pay expiry check

시험 DB 에서 먼저 실행하고, 게임 서버가 한가할 때 적용한다.
Run them on a test database first and apply them when the game servers are idle.

### 로그 / Logs

- 화면과 `log\A3ASD-yyyyMMdd.log` 에 같은 내용이 남는다. `[ERROR]` 는 빨강, `[WARN]` 은 주황.
  The window and `log\A3ASD-yyyyMMdd.log` get the same lines. `[ERROR]` is red, `[WARN]` is orange.
- 시작 줄에 빌드 시각(`build=`), 설정 파일, DB, 인증 방식이 찍힌다.
  The start line shows the build time (`build=`), config file, database and authentication mode.
- `wire=1`: `C2S op=2011 id=…` / `S2C op=3011 id=… result=OK` 요약 / summaries.
- 설정과 상관없이 항상 남는 것: DB 오류(`[ERROR] … DB error`), 지원하지 않는 요청(`[WARN] … UNKNOWN opcode`), 형식 오류(`[WARN] … bad format`), DB 상태 변화.
  Always logged regardless of settings: DB errors (`[ERROR] … DB error`), unsupported requests (`[WARN] … UNKNOWN opcode`), malformed requests (`[WARN] … bad format`) and DB state changes.
- 계정 테이블 포트에서는 로그인 비교값(`c_headera`)을 `<hidden>` 으로 가린다. 그래도 `payload=1` 로그는 민감 정보로 다룬다.
  On account-table ports the login comparison value (`c_headera`) is replaced with `<hidden>`. Still treat `payload=1` logs as sensitive.

## 지원 범위 / Supported requests

| 요청 / Request | 응답 / Reply | 동작 / Action | 보내는 서버 / Sent by |
|---|---|---|---|
| `2021` | `3012` | 계정 로드(9컬럼) / account load (9 columns) | LoginServer |
| `2011` | `3011` | 캐릭터·창고 로드(11컬럼) / character or storage load (11 columns) | MainServer |
| `2071` | `3071` | 저장 — **UPDATE 전용**, 없는 행은 만들지 않음(101) / save — **UPDATE only**, never inserts (101) | MainServer |
| `2002` | `3002` | 생성 — 이미 있으면 101 / create — 101 if it exists | MainServer |
| `2048` | `3003` | `c_status` 갱신 — 캐릭터 삭제는 `X` 표시(행 유지) / set `c_status` — character delete marks `X` (row kept) | MainServer |

- 위 5종이 LoginServer·MainServer 가 실제로 보내는 요청 전부다. 원래 ASD 의 그 밖의 요청은 구현하지 않았고, 오면 `3000 0 999 UNKNOWN_OP` 로 답하고 연결은 유지하며 로그에 원문을 남긴다.
  These five are all the requests LoginServer and MainServer actually send. Other original ASD requests are not implemented; they get `3000 0 999 UNKNOWN_OP`, the connection stays open and the raw request is logged.
- 오류 코드: `101` 행 없음/중복, `901` 인자 부족, `902` DB 오류, `903` 생성 전 확인 실패. 문구는 원래 ASD 와 같다.
  Error codes: `101` no row / duplicate, `901` too few arguments, `902` DB error, `903` pre-create check failed. Reply text matches the original ASD.
- DB 는 하나만 쓴다. 원래 ASD 의 샤드(여러 DSN) 구성은 지원하지 않는다.
  A single database is used. The original ASD's sharded (multi-DSN) layout is not supported.
- 레코드 컬럼 / Record columns: `c_id, c_sheadera, c_sheaderb, c_sheaderc, c_headera, c_headerb, c_headerc, d_cdate, d_udate, c_status, m_body`.
- 문자열 인코딩은 게임 서버와 같은 시스템 ANSI 코드 페이지(`GetACP()`, 한국어 = CP949)다. 서버의 Windows 시스템 로캘을 게임 서버와 맞춘다.
  Strings use the same system ANSI code page as the game servers (`GetACP()`, Korean = CP949). Match the Windows system locale to the game servers.

## 자체 테스트 (DB 불필요) / Self-test (no database needed)

메모리 모드로 띄운 뒤 Node.js 테스트를 돌린다.
Start A3ASD in memory mode, then run the Node.js tests.

```
src\bin\Release\net10.0-windows\A3ASD.exe <absolute repo path>\test\H3DB.memory.ini
node test\wiretest.js     # 29/29
node test\paytest.js      # 16/16
```

테스트 ini 는 7770/8880/9990/13000 포트를 쓰므로 실제 A3ASD·ASD 가 떠 있지 않은 PC 에서 돌린다. 끝나면 창을 닫는다.
The test ini uses ports 7770/8880/9990/13000, so run it on a machine where no real A3ASD or ASD is running. Close the window when done.

## 보안 / Security

- H3DB·Pay 포트에는 인증도 암호화도 없다. 게임 서버만 접근할 수 있도록 방화벽으로 막는다.
  The H3DB and Pay ports have no authentication or encryption. Firewall them so only the game servers can connect.
- DB 계정은 필요한 테이블에 대한 읽기·쓰기 권한만 주는 것을 권장한다.
  Give the database login read/write rights on the required tables only.

## 소스 구성 / Source layout

| 파일 / File | 내용 / Contents |
|---|---|
| `src/Program.cs` | 진입점, 설정 로드 / entry point, config load |
| `src/AsdServer.cs` | 서버 코어: 포트별 TCP 리스너, 상태 카운터, 로그 정리 / server core: per-port TCP listeners, stats, log cleanup |
| `src/Protocol.cs` | H3DB 프로토콜: 요청 분해·응답 조립·오류 코드, 줄 단위 수신 버퍼 / H3DB protocol: request parsing, replies, error codes, line reader |
| `src/PayServer.cs` | 계정 만료 확인(Pay) 바이너리 프로토콜 / account expiry (Pay) binary protocol |
| `src/SqlRecordStore.cs` | SQL Server 저장소(파라미터 쿼리) / SQL Server store (parameterized queries) |
| `src/InMemoryStore.cs` | 메모리 저장소(자체 테스트용) / in-memory store (self-test) |
| `src/IRecordStore.cs` | 저장소 인터페이스와 DTO / store interface and DTOs |
| `src/Config.cs` | `H3DB.ini` 파서 / parser |
| `src/MainForm.cs` | WinForms 로그 창 / log window |
| `docs/` | 게임 서버별 프로토콜·설정 문서 / protocol and setup per game server |
| `sql/` | 인덱스·만료 컬럼 스크립트 / index and expiry-column scripts |
| `test/`, `examples/` | 자체 테스트, Pay 예제 클라이언트 / self-tests, Pay example client |

## AI 사용 고지 / AI disclosure

이 프로젝트는 원래 ASD 의 동작을 바탕으로 코드를 작성하고 정리하는 과정에서 **AI(Anthropic Claude, Claude Code)를 사용해 리팩토링했다.**
코드와 문서는 사람이 검토했고, 자체 테스트와 실제 게임 서버 환경에서 동작을 확인했다.

This project was **refactored with the help of AI (Anthropic Claude, Claude Code)** while writing and cleaning up the code based on the original ASD's behavior.
The code and documents were reviewed by a human and verified with the self-tests and in a real game server environment.

## 면책 / Disclaimer

- 이 저장소는 비공식 호환 구현이며 A3 및 원래 ASD 미들웨어의 권리자와 관계가 없다. 관련 상표와 권리는 각 권리자에게 있다.
  This is an unofficial compatible implementation, not affiliated with the rights holders of A3 or the original ASD middleware. All trademarks and rights belong to their owners.
- 원래 프로그램의 바이너리·소스·게임 데이터는 포함하지 않는다.
  No binaries, source code or game data of the original programs are included.
- 운영 DB 에 적용하기 전에 반드시 시험 환경에서 확인하고 백업한다. 소프트웨어는 MIT 라이선스에 따라 "있는 그대로" 제공된다.
  Test in a staging environment and back up before using it on a production database. The software is provided "as is" under the MIT License.

## 라이선스 / License

[MIT](LICENSE)
