# LoginServer ↔ A3ASD 계정 만료 확인 채널 (Pay) / Account expiry check channel (Pay)

LoginServer 는 로그인을 허용하기 직전에 `[PAYSERVER]` 연결로 계정 만료 여부를 묻는다.
A3ASD 는 이 Pay 서버 역할을 하며, 계정 테이블의 만료 시각 컬럼(기본 `account.d_edate`)으로 판정한다.

Right before allowing a login, LoginServer asks over its `[PAYSERVER]` link whether the account has expired.
A3ASD plays this Pay server role and decides from an expiry-time column in the account table (default `account.d_edate`).

- **Pay 가 연결되어 있지 않으면 LoginServer 는 확인을 건너뛰고 로그인을 허용한다.**
  **If Pay is not connected, LoginServer skips the check and allows the login.**
- 연결된 상태에서 응답이 없으면 그 사용자의 로그인은 멈춘다(LoginServer 에 시간 초과가 없다). 그래서 A3ASD 는 DB 오류가 나도 반드시 응답한다.
  If Pay is connected but does not reply, that user's login hangs (LoginServer has no timeout). A3ASD therefore always replies, even on DB errors.
- 거부되는 경우는 응답 결과가 정확히 `000` 일 때뿐이다.
  A login is rejected only when the result is exactly `000`.
- 확인은 로그인할 때 한 번뿐이다. 접속 중 만료 처리, 로그아웃 통지, 사용 시간 보고는 없다.
  The check happens once per login. There is no in-game expiry, logout notice or usage reporting.
- 접속 주체를 제한하지 않는다. 같은 프로토콜로 다른 서버도 만료를 조회할 수 있다.
  Any client may connect; other servers can query expiry with the same protocol.

## 1. 설정 / Settings

`LoginServer.ini`:

```ini
[PAYSERVER]
IP=127.0.0.1
PORT=3000
```

키가 없으면 기본값 `127.0.0.1:3000` 으로 접속을 시도한다. 끊기면 5초마다 다시 접속한다.
Without these keys LoginServer tries the default `127.0.0.1:3000`. A dropped link is retried every 5 seconds.

A3ASD `H3DB.ini`:

```ini
[pay]
port=3000
table=account
column=d_edate
```

| 키 / Key | 기본값 / Default | 의미 / Meaning |
|---|---|---|
| `port` | 0 | 듣는 포트. **0 = 끔**(LoginServer 는 만료 확인 없이 로그인시킨다) / listening port. **0 = off** (LoginServer logs users in without the check) |
| `table` | `account` | 만료 컬럼이 있는 테이블 / table holding the expiry column |
| `column` | `d_edate` | 만료 시각 컬럼 / expiry time column |

DB 준비 — 컬럼 추가(`sql/add_d_edate.sql`) / Database preparation — add the column (`sql/add_d_edate.sql`):

```sql
ALTER TABLE account ADD d_edate datetime NULL;
UPDATE account SET d_edate = '2026-12-31 23:59:59' WHERE c_id = '<account>';
```

- 컬럼은 맨 뒤에 추가된다. 열 목록 없이 `INSERT INTO account VALUES (...)` 를 쓰는 홈페이지·운영 도구가 있으면 컬럼 수가 달라져 실패하므로 먼저 확인한다.
  The column is appended at the end. Websites or admin tools that use `INSERT INTO account VALUES (...)` without a column list will break because the column count changes, so check them first.
- A3ASD 는 요청마다 컬럼을 조회하므로 컬럼을 추가하면 재시작 없이 바로 적용된다.
  A3ASD looks the column up on every request, so adding it takes effect without a restart.

> **주의:** `[pay] port=0` 인데 LoginServer 의 `[PAYSERVER]` 주소(기본 `127.0.0.1:3000`)에 다른 프로그램이 떠 있으면, LoginServer 는 그 프로그램을 Pay 서버로 여기고 응답을 기다리며 모든 로그인이 멈춘다. 같은 서버에서 그 포트를 다른 용도로 쓰지 않는다.
>
> **Warning:** if `[pay] port=0` but some other program listens on LoginServer's `[PAYSERVER]` address (default `127.0.0.1:3000`), LoginServer treats it as the Pay server, waits for replies, and every login hangs. Do not use that port for anything else on the same machine.

## 2. 패킷 형식 / Packet format

바이너리, 리틀엔디안, 평문, 인증 없음(방화벽으로 접근을 막는다). 문자열은 고정 길이 필드에 NUL 로 끝내며 인코딩은 시스템 ANSI(한국어 CP949)다.
Binary, little-endian, plain text, no authentication (restrict access with a firewall). Strings are NUL-terminated in fixed-size fields, in the system ANSI code page (CP949 on Korean Windows).

공통 헤더 10바이트 / Common 10-byte header:

| 오프셋 / Offset | 크기 / Size | 내용 / Contents |
|---|---|---|
| `+00` | 4 | 패킷 전체 길이(자기 포함) / total packet length (inclusive) |
| `+04` | 4 | 사용자 번호(응답에 그대로 돌아온다) / user number (echoed in the reply) |
| `+08` | 1 | 종류, 항상 `2` / type, always `2` |
| `+09` | 1 | 부종류 / subtype |

| 패킷 / Packet | 방향 / Direction | 종류/부종류 / Type/sub | 길이 / Length | 응답 / Reply |
|---|---|---|---|---|
| P1 접속 인사 / hello | 클라이언트 → 서버 / client → server | `2 / 0xE0` | `0x0A` | 없음(생략 가능) / none (optional) |
| P2 만료 확인 요청 / expiry check | 클라이언트 → 서버 / client → server | `2 / 0xE1` | `0x2F` | P3 |
| P3 만료 확인 응답 / expiry reply | 서버 → 클라이언트 / server → client | `2 / 0xE0` | `0x41` | - |

### P1 접속 인사 / Hello

LoginServer 가 접속 직후 한 번 보낸다.
LoginServer sends it once right after connecting.

```text
0A 00 00 00  00 00 00 00  02 E0
```

### P2 만료 확인 요청 (0x2F 바이트) / Expiry check request (0x2F bytes)

| 오프셋 / Offset | 크기 / Size | 내용 / Contents |
|---|---|---|
| `+0A` | 21 | 계정 ID(조회 키) / account ID (lookup key) |
| `+1F` | 16 | 클라이언트 IP 문자열(응답에 복사만 한다) / client IP string (only copied to the reply) |

비밀번호는 보내지 않는다.
No password is sent.

예: 사용자 번호 2, ID `paypast`, IP `1.2.3.4` / Example: user number 2, ID `paypast`, IP `1.2.3.4`

```text
0000  2F 00 00 00 02 00 00 00 02 E1 70 61 79 70 61 73
0010  74 00 00 00 00 00 00 00 00 00 00 00 00 00 00 31
0020  2E 32 2E 33 2E 34 00 00 00 00 00 00 00 00 00
```

### P3 만료 확인 응답 (0x41 바이트) / Expiry check reply (0x41 bytes)

| 오프셋 / Offset | 크기 / Size | 형식 / Type | 내용 / Contents |
|---|---|---|---|
| `+0A` | 21 | 문자열 / string | P2 의 계정 ID / account ID from P2 |
| `+1F` | 16 | 문자열 / string | P2 의 IP / IP from P2 |
| `+2F` | 8 | double | 날짜(Delphi `TDateTime`, 1899-12-30 기준 일수). LoginServer 는 쓰지 않는다 / date (Delphi `TDateTime`, days since 1899-12-30); unused by LoginServer |
| `+37` | 1 | u8 | 결과 길이, `3` / result length, `3` |
| `+38` | 3 | ASCII | `100` 허용 / allow, `000` 거부 / reject |
| `+3B` | 6 | | `0` |

거부 예 — 위 P2 에 대한 응답(`d_edate` = 2000-01-01 00:00:00):
Reject example — reply to the P2 above (`d_edate` = 2000-01-01 00:00:00):

```text
0000  41 00 00 00 02 00 00 00 02 E0 70 61 79 70 61 73
0010  74 00 00 00 00 00 00 00 00 00 00 00 00 00 00 31
0020  2E 32 2E 33 2E 34 00 00 00 00 00 00 00 00 00 00
0030  00 00 00 C0 D5 E1 40 03 30 30 30 00 00 00 00 00
0040  00
```

허용 예 — ID `payfuture` 요청에 대한 응답(`d_edate` = 2099-12-31 23:59:59):
Allow example — reply for ID `payfuture` (`d_edate` = 2099-12-31 23:59:59):

```text
0000  41 00 00 00 02 00 00 00 02 E0 70 61 79 66 75 74
0010  75 72 65 00 00 00 00 00 00 00 00 00 00 00 00 31
0020  2E 32 2E 33 2E 34 00 00 00 00 00 00 00 00 00 1C
0030  DD F3 FF AF D5 F1 40 03 31 30 30 00 00 00 00 00
0040  00
```

## 3. 판정 규칙 / Decision rules

| 상황 / Situation | 결과 / Result | `+2F` 날짜 / date | 로그 표시 / Log |
|---|---|---|---|
| DB 현재 시각(`getdate()`) >= 만료 시각 / DB time (`getdate()`) >= expiry | `000` 거부 / reject | 만료 시각 / expiry | `expire=… result=000` |
| 만료 시각이 미래 / expiry in the future | `100` 허용 / allow | 만료 시각 / expiry | `expire=… result=100` |
| 값이 NULL / value is NULL | `100` 허용 / allow | DB 현재 시각 / DB time | `no-limit` |
| 계정 없음 / no such account | `100` 허용 / allow | A3ASD 현재 시각 / A3ASD time | `no-limit` |
| 컬럼 없음 / no column | `100` 허용 / allow | A3ASD 현재 시각 / A3ASD time | `no-column` (상태가 바뀔 때 `[WARN]` 1회 / one `[WARN]` per state change) |
| 날짜가 아닌 값 / not a date | `100` 허용 / allow | A3ASD 현재 시각 / A3ASD time | `bad-value`, `[WARN]` |
| DB 오류 / DB error | `100` 허용 / allow | A3ASD 현재 시각 / A3ASD time | `db-error`, `[ERROR]` |

- DB 오류 때도 로그인을 막지 않으려고 허용으로 응답한다. 장애 중에는 만료된 계정도 통과한다.
  DB errors are answered with "allow" so logins are not blocked; during an outage expired accounts also get in.
- `date` 형식 컬럼이면 그날 0시에 만료된다.
  With a `date` column the account expires at midnight at the start of that day.
- 요약 로그 `id=… <표시> result=…` 는 `[log] wire=1` 일 때만 남는다. `[WARN]`·`[ERROR]` 는 설정과 상관없이 남는다.
  The summary line `id=… <label> result=…` is written only with `[log] wire=1`. `[WARN]` and `[ERROR]` are always written.

## 4. 연결 처리 / Connection handling

- 한 연결로 요청을 여러 번 보낼 수 있다. 조각 수신과 여러 패킷 연속 수신을 모두 처리한다.
  One connection can carry many requests. Fragmented and back-to-back packets are both handled.
- 한 연결 안에서는 요청 순서대로 응답한다. 사용자 번호를 요청마다 다르게 넣으면 응답을 맞춰 받을 수 있다.
  Replies come back in request order on a connection; use a different user number per request to match them.
- 길이가 10 미만이거나 `0x2000` 을 넘으면 연결을 끊는다. 모르는 종류·부종류는 응답 없이 무시하고 연결은 유지한다.
  A length below 10 or above `0x2000` closes the connection. Unknown types or subtypes are ignored without a reply and the connection stays open.

## 5. LoginServer 쪽 흐름 / LoginServer flow

```
계정 조회(3012) 통과 / account lookup (3012) passed → AllowStatus 통과 / passed
  ├ Pay 연결 안 됨 / Pay not connected → 로그인 진행 / login proceeds
  └ Pay 연결됨 / Pay connected        → P2 전송 / send P2 → P3 대기 / wait for P3
        ├ 결과 000 / result 000 → 거부 / reject: "This Account Expires"
        ├ 그 외 / anything else  → 로그인 진행 / login proceeds
        └ 응답 없음 / no reply   → 대기 지속(시간 초과 없음) / waits forever (no timeout)
```

- A3ASD 가 꺼져 있으면 LoginServer 는 Pay 미연결로 보고 만료 확인 없이 통과시킨다.
  If A3ASD is down, LoginServer sees Pay as disconnected and lets users in without the check.
- 요청을 받은 뒤 응답 전에 연결이 끊기면 그 사용자의 로그인은 멈춘다. 사용자가 다시 접속해야 한다.
  If the link drops after a request but before the reply, that user's login hangs and the user must reconnect.

## 6. 예제 클라이언트 / Example client

```text
> node examples\payclient.js 127.0.0.1 3000 paypast 1.2.3.4 2
410000000200000002e0706179706173
74000000000000000000000000000031
2e322e332e3400000000000000000000
000000c0d5e140033030300000000000
00
{ user: 2, id: 'paypast', ip: '1.2.3.4', date: '2000-01-01 00:00:00', result: '000' } EXPIRED
```

사용법: `node examples/payclient.js <host> <port> <id> [ip] [user]`. 예제는 ID 를 `latin1` 로 쓰므로 영문·숫자 ID 용이다(한글 ID 는 CP949 바이트로 넣어야 한다).
Usage: `node examples/payclient.js <host> <port> <id> [ip] [user]`. The example writes the ID as `latin1`, so it is for ASCII IDs only (Korean IDs must be sent as CP949 bytes).
