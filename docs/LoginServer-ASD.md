# LoginServer ↔ A3ASD 계정 조회 채널 / Account lookup channel

LoginServer 는 `[LOGINASD]` 연결로 A3ASD 의 계정 포트(기본 7770, 테이블 `account`)에 붙어 로그인할 때마다 계정을 조회한다.
이 연결에서 쓰는 메시지는 **요청 `2021` 과 응답 `3012` 두 가지뿐**이다.

LoginServer connects to A3ASD's account port (default 7770, table `account`) through its `[LOGINASD]` link and looks up the account on every login.
**Only two messages are used on this link: request `2021` and reply `3012`.**

- 연결 방향: LoginServer → A3ASD (LoginServer 가 TCP 클라이언트).
  Direction: LoginServer → A3ASD (LoginServer is the TCP client).
- **비밀번호 비교는 LoginServer 가 한다.** A3ASD 는 `c_headera` 값을 그대로 돌려줄 뿐 검증하지 않는다.
  **LoginServer compares the password.** A3ASD only returns the `c_headera` value; it does not verify anything.
- 계정 행을 만들거나 고치는 요청은 오지 않는다. 계정 생성은 홈페이지나 운영 도구가 DB 에 직접 한다.
  No request creates or changes account rows. Accounts are created directly in the DB by the website or admin tools.

## 1. LoginServer 설정 / LoginServer settings

`LoginServer.ini`:

```ini
[LOGINASD]
COUNT=1
IP0=127.0.0.1
PORT0=7770
```

| 키 / Key | 기본값 / Default | 의미 / Meaning |
|---|---|---|
| `COUNT` | 0 | 연결 수. **0 이면 연결이 없고 로그인 요청은 응답 없이 버려진다** / number of links. **0 = no link; login requests are dropped without a reply** |
| `IP<n>` | `127.0.0.1` | A3ASD 주소 / A3ASD address |
| `PORT<n>` | 8000 | A3ASD 계정 포트. A3ASD 기본 구성은 7770 / A3ASD account port; A3ASD default is 7770 |

- 항목은 최대 16개다. 같은 `IP:PORT` 는 연결 하나로 합쳐진다.
  Up to 16 entries. Identical `IP:PORT` pairs share one link.
- 끊긴 연결은 5초마다 다시 접속한다. A3ASD 를 재시작해도 몇 초 안에 자동으로 다시 붙는다.
  A dropped link is retried every 5 seconds, so a restarted A3ASD is picked up within seconds.
- `COUNT` 가 2 이상이면 접속 여부와 상관없이 대기열이 짧은 연결로 요청이 간다. A3ASD 하나면 `COUNT=1` 을 권장한다.
  With `COUNT` ≥ 2 a request goes to the link with the shortest queue whether or not it is connected. With a single A3ASD use `COUNT=1`.

## 2. 공통 규칙 / Common rules

- ASCII 텍스트 한 줄 = 메시지 하나. 필드 구분은 공백 1개, 줄 끝은 `\r\n`.
  One ASCII text line = one message. Fields are separated by a single space; lines end with `\r\n`.
- 문자열 인코딩은 시스템 ANSI 코드 페이지(한국어 Windows = CP949). 길이 헤더·순번·체크섬은 없다.
  Strings use the system ANSI code page (CP949 on Korean Windows). No length header, sequence number or checksum.
- 빈 값과 NULL 은 `-` 로 보낸다.
  Empty and NULL values are sent as `-`.
- 날짜는 `yyyyMMdd.HHmmss`, NULL 날짜는 `18991230.000000`.
  Dates are `yyyyMMdd.HHmmss`; a NULL date is `18991230.000000`.
- 실패 응답 문구의 공백·줄바꿈은 이스케이프한다: `\_`→`\_0`, CR→`\_1`, LF→`\_2`, 공백→`\_3`.
  Spaces and line breaks in failure text are escaped: `\_`→`\_0`, CR→`\_1`, LF→`\_2`, space→`\_3`.

## 3. 요청 `2021` — 계정 조회 / Request `2021` — account lookup

```
2021 <id>\r\n
```

| 필드 / Field | 의미 / Meaning |
|---|---|
| `2021` | 계정 조회. 테이블은 연결한 포트로 정해진다 / account lookup; the table is chosen by the port |
| `<id>` | 로그인 입력 ID(최대 20바이트). `WHERE c_id = <id>` 조회 키 / login ID typed by the user (max 20 bytes); the `WHERE c_id = <id>` key |

A3ASD 는 ID 를 받은 그대로 조회 키로 쓴다(언이스케이프하지 않는다).
A3ASD uses the ID exactly as received (no unescaping).

## 4. 응답 `3012` / Reply `3012`

### 성공 / Success

```
3012 1 <c_id> <c_sheadera> <c_sheaderb> <c_sheaderc> <c_headera> <c_headerb> <c_headerc> <d_udate> <c_status>\r\n
```

| 토큰 / Token | 컬럼 / Column | LoginServer 처리 / LoginServer use |
|---|---|---|
| 0 | | `3012` |
| 1 | | `1` = 성공 / success |
| 2 | `c_id` | 입력 ID 와 비교 / compared with the typed ID |
| 3~5 | `c_sheadera~c` | 사용 안 함 / unused |
| 6 | `c_headera` | **입력 비밀번호와 비교 / compared with the typed password** |
| 7~8 | `c_headerb~c` | 사용 안 함 / unused |
| 9 | `d_udate` | 사용 안 함 / unused |
| 10 | `c_status` | `AllowStatus.SDB` 에서 접속 허용 여부 확인 / checked against `AllowStatus.SDB` |

ID·비밀번호 비교는 대소문자를 구분한다. DB 조회는 collation 에 따라 대소문자를 무시할 수 있으므로, 입력 `MyID` 로 `myid` 행을 찾아도 ID 비교에서 거부된다.
ID and password comparisons are case-sensitive. The DB lookup may be case-insensitive depending on collation, so typing `MyID` can find the row `myid` and still be rejected by the ID comparison.

### 실패 / Failure

```
3012 0 <code> <text>\r\n
```

| 응답 / Reply | 조건 / Condition |
|---|---|
| `3012 0 101 해당\_3Keyword가\_3없음` | 계정 없음 / no such account |
| `3012 0 902 Query\_3실행\_3오류` | DB 오류 / database error |
| `3012 0 901 MSG\_3형식\_3오류` | 인자 부족 / too few arguments |

LoginServer 는 두 번째 토큰이 `1` 인지만 본다. 코드와 문구는 읽지 않는다.
LoginServer only checks whether the second token is `1`; it ignores the code and text.

## 5. LoginServer 판정 순서 / LoginServer decision order

| 순서 / Step | 조건 / Condition | 사용자에게 보이는 결과 / What the user sees |
|---|---|---|
| 1 | 응답이 `3012` 가 아님 / reply is not `3012` | 다시 시도 요청 / "please try again" |
| 2 | 두 번째 토큰이 `1` 이 아님(101·901·902 모두) / second token is not `1` (101, 901, 902) | ID·비밀번호 확인 요청 / "check your ID and password" |
| 3 | ID 또는 비밀번호 불일치 / ID or password mismatch | 비밀번호 오류 / wrong password |
| 4 | 이미 접속 중 / already logged in | 중복 접속, 기존 세션 끊기 / duplicate login, old session kicked |
| 5 | `c_status` 가 `AllowStatus` 에 없음 / `c_status` not in `AllowStatus` | 접속 불가 / cannot connect |
| 6 | 통과 / passed | Pay 연결이 있으면 만료 확인([LoginServer-Pay.md](LoginServer-Pay.md)), 없으면 로그인 진행 / expiry check if Pay is linked, otherwise login proceeds |

**DB 장애(`3012 0 902`)도 2번으로 처리되어 사용자에게는 ID·비밀번호 오류처럼 보인다.** 원인은 A3ASD 로그(`[ERROR] ... DB error`, 상태 줄의 `DB FAIL`)로 구분한다.
**A database failure (`3012 0 902`) also lands in step 2, so users see it as a wrong ID or password.** Tell them apart with the A3ASD log (`[ERROR] ... DB error`, `DB FAIL` in the status bar).

## 6. 흐름 제어 / Flow control

- LoginServer 는 연결마다 요청을 하나씩 보내고, 응답을 받거나 시간 초과가 날 때까지 다음 요청을 보내지 않는다.
  LoginServer sends one request per link at a time and waits for the reply or a timeout before sending the next.
- 요청과 응답을 잇는 값이 없다. 응답은 대기열 맨 앞 요청의 것으로 본다.
  Nothing ties a reply to its request; a reply is matched to the request at the head of the queue.
- **시간 초과는 3초**다. 넘으면 그 요청을 버리고 사용자에게 아무것도 보내지 않는다. 재시도도 없다. DB 응답이 3초를 넘으면 그 사용자의 로그인은 응답 없이 끝난다.
  **The timeout is 3 seconds.** The request is then dropped with nothing sent to the user and no retry. If the DB takes longer than 3 seconds, that login silently fails.
- keepalive 는 없다. 유휴 중에는 오가는 메시지가 없다.
  There is no keepalive; an idle link carries no traffic.
- **응답 한 줄과 끝의 `\r\n` 은 한 번에 보내야 한다.** LoginServer 는 LF 가 올 때까지 줄을 기다리는데, 줄 끝만 늦게 오면 멈출 수 있다. A3ASD 는 응답 전체를 한 번의 쓰기로 보낸다.
  **A reply line and its `\r\n` must be sent together.** LoginServer waits for the LF and can stall if only the line ending arrives late. A3ASD sends each reply in a single write.

## 7. 운영 주의 / Operational notes

- `c_headera` 에 공백이 있으면 토큰 위치가 밀려 로그인할 수 없다.
  A space inside `c_headera` shifts the tokens and makes login impossible.
- `c_headera` 가 NULL 이면 `-` 가 전달되므로, 비밀번호 `-` 로 로그인이 통과된다. 빈 비밀번호 계정을 두지 않는다.
  A NULL `c_headera` is sent as `-`, so the password `-` would log in. Do not keep accounts with an empty password.
- `3012` 응답의 토큰 6 은 로그인 비교값이다. A3ASD 는 계정 테이블 포트의 로그에서 이 값을 `<hidden>` 으로 가린다. 그래도 `[log] payload=1` 로그와 패킷 캡처는 민감 정보로 다룬다.
  Token 6 of `3012` is the login comparison value. A3ASD replaces it with `<hidden>` in logs on account-table ports. Still treat `[log] payload=1` logs and packet captures as sensitive.
