# MainServer ↔ A3ASD 캐릭터·창고 채널 / Character and storage channel

MainServer 는 두 종류의 연결로 A3ASD 에 붙는다.
MainServer connects to A3ASD over two kinds of links.

| 연결 / Link | A3ASD 포트(기본) / Port (default) | 테이블 / Table | 용도 / Purpose |
|---|---|---|---|
| `CharacDB` | 8880 | `charac0` | 캐릭터 / characters |
| `StorageDB` | 9990 | `ItemStorage0` | 계정 창고 / account storage |

MainServer 가 보내는 요청은 4종이다.
MainServer sends four kinds of requests.

| 요청 / Request | 응답 / Reply | 캐릭터 연결 / Character link | 창고 연결 / Storage link |
|---|---|---|---|
| `2011` 로드 / load | `3011` | 캐릭터 접속 / character login | 창고를 처음 열 때 / first time storage is opened |
| `2002` 생성 / create | `3002` | 캐릭터 만들기 / character creation | 첫 창고 만들기 / first storage creation |
| `2071` 저장 / save | `3071` | 주기 저장(약 11분)·종료 저장 / periodic save (about 11 min) and logout save | 창고 변경 / storage change |
| `2048` 상태 변경 / set status | `3003` | 캐릭터 삭제 / character delete | 없음 / none |

## 1. MainServer 설정 / MainServer settings

`SvrInfo.ini`:

```ini
[SvrInfo]
CharacDBServerNum=1
CharacDBIP=127.0.0.1
CharacDBPort=8880
StorageDBServerNum=1
StorageDBIP=127.0.0.1
StorageDBPort=9990
```

| 키 / Key | 기본값 / Default | 의미 / Meaning |
|---|---|---|
| `CharacDBServerNum` | 0 | 캐릭터 연결 수. 1 미만이면 연결하지 않는다 / number of character links; below 1 = none |
| `CharacDBIP` / `CharacDBPort` | `loopback` / 8880 | 주소·포트 목록(공백 구분) / address and port lists (space-separated) |
| `StorageDBServerNum` | 0 | 창고 연결 수 / number of storage links |
| `StorageDBIP` / `StorageDBPort` | `loopback` / 9990 | 주소·포트 목록 / address and port lists |

- IP 는 **숫자 주소로** 쓴다. 호스트 이름은 해석하지 않는다(기본값 `loopback` 그대로면 접속이 실패한다).
  Use **numeric IP addresses**. Host names are not resolved (the default `loopback` fails to connect).
- 연결 수가 2 이상이면 요청마다 연결을 돌아가며 쓴다.
  With two or more links, requests rotate across them.

## 2. 전송 형식 / Wire format

```
요청 / request : <opcode> <body>\r          ← CR 만, LF 없음 / CR only, no LF
응답 / reply   : <opcode> <result> ...\r\n
```

- MainServer 는 응답을 `\r` 까지 한 줄로 자른다. **`\r` 이 없으면 메시지가 끝나지 않는다.** A3ASD 는 항상 `\r\n` 으로 끝낸다.
  MainServer cuts a reply at `\r`. **Without `\r` the message never ends.** A3ASD always ends replies with `\r\n`.
- 필드는 공백으로 자르고 연속 공백은 하나로 합친다. 그래서 빈 값은 반드시 `-` 로 보낸다. 숫자 필드의 `-` 는 0 으로 읽힌다.
  Fields are split on spaces and repeated spaces collapse, so empty values must be sent as `-`. A `-` in a numeric field reads as 0.
- 성공 판정은 두 번째 토큰(`1`/`0`)이다. `30xx 0 …` 은 실패다.
  Success is decided by the second token (`1`/`0`); `30xx 0 …` is a failure.
- 날짜는 `yyyyMMdd.HHmmss`, NULL 날짜는 `18991230.000000`. MainServer 는 `d_udate` 로 마지막 저장 후 경과 시간을 계산하므로 형식이 바뀌면 안 된다.
  Dates are `yyyyMMdd.HHmmss`; a NULL date is `18991230.000000`. MainServer uses `d_udate` to compute time since the last save, so the format must not change.

### 이스케이프 / Escaping

MainServer 는 본문을 저장할 때 이스케이프하고, 로드할 때 되돌린다. A3ASD 는 이 값을 해석하지 않고 그대로 저장·반환하므로 DB 의 `m_body` 에도 이스케이프된 형태로 들어 있다.
MainServer escapes the body when saving and unescapes it when loading. A3ASD stores and returns it untouched, so `m_body` in the DB is also in escaped form.

| 원문 / Raw | 와이어 / Wire |
|---|---|
| `\_` | `\_0` |
| CR | `\_1` |
| LF | `\_2` |
| 공백 / space | `\_3` |

### 크기 제한 (MainServer 쪽) / Size limits (MainServer side)

| 항목 / Item | 크기 / Size | 초과 시 / If exceeded |
|---|---|---|
| 요청 본문 / request body | 6,143바이트 / bytes | MainServer 가 **말없이 자른다** / **silently truncated** by MainServer |
| 키(`c_id`) / key | 20바이트 / bytes | 잘린다 / truncated |
| 응답 한 줄 / `m_body` / reply line | 8,192바이트 / bytes | 넘지 않아야 한다 / must not exceed |

A3ASD 자체는 한 줄 1MB 까지 받는다.
A3ASD itself accepts lines up to 1 MB.

## 3. 요청 / Requests

### `2011` 로드 / load

```
2011 <c_id>\r
```

캐릭터 연결은 캐릭터 이름, 창고 연결은 계정 ID 를 키로 쓴다.
The character link uses the character name as the key; the storage link uses the account ID.

### `2002` 생성 / create

캐릭터 / Character:

```
2002 <name> <account> <sb> <sc> <10 stats> <map;cell> <money> A <m_body>\r
```

창고(`3011 0 101` 을 받으면 MainServer 가 자동으로 보낸다) / Storage (sent automatically after `3011 0 101`):

```
2002 <account> 0 0 0 0 0 0 A 0;0;0;0\r
```

### `2071` 저장 / save

캐릭터는 `2002` 와 같은 형식이다. 창고:
Characters use the same format as `2002`. Storage:

```
2071 <account> 0 0 0 0 0 <storage money> A <items>\r
```

저장은 **UPDATE 전용**이다. 없는 행을 저장하면 `3071 0 101` 이고 행은 만들어지지 않는다. 생성은 `2002` 로만 한다.
Save is **UPDATE only**. Saving a missing row returns `3071 0 101` and creates nothing; rows are created only by `2002`.

### `2048` 상태 변경 / set status

```
2048 <name> X\r
```

캐릭터 삭제에서만 보내며 값은 항상 `X` 다. **행을 지우지 않고 `c_status` 만 `X` 로 바꾼다**(soft delete). `d_udate` 는 바뀌지 않는다.
Sent only when a character is deleted, always with `X`. **The row is not deleted; only `c_status` becomes `X`** (soft delete). `d_udate` is left unchanged.

## 4. 응답 / Replies

### `3011 1` 로드 성공 / load success

```
3011 1 <c_id> <sa> <sb> <sc> <ha> <hb> <hc> <d_cdate> <d_udate> <c_status> <m_body>\r\n
```

| 토큰 / Token | 컬럼 / Column | 캐릭터에서의 의미 / Meaning for a character |
|---|---|---|
| 2 | `c_id` | 캐릭터 이름 / character name |
| 3 | `c_sheadera` | 계정 ID / account ID |
| 4~5 | `c_sheaderb`, `c_sheaderc` | 직업·레벨 계열 값 / class and level values |
| 6 | `c_headera` | 능력치 10개(`;` 구분) / 10 stats (`;`-separated) |
| 7 | `c_headerb` | `맵;셀` / `map;cell` |
| 8 | `c_headerc` | 소지금 계열 값 / money value |
| 9 | `d_cdate` | 생성 시각(MainServer 는 쓰지 않음) / creation time (unused by MainServer) |
| 10 | `d_udate` | 마지막 저장 시각 / last save time |
| 11 | `c_status` | `X`·`Y` 면 없는 캐릭터로 처리 / `X` or `Y` = treated as missing |
| 12 | `m_body` | 나머지 데이터 / remaining data |

창고 행은 `c_headerc` = 창고 돈, 나머지 헤더 `0`, `c_status` = `A` 다.
A storage row has `c_headerc` = storage money, the other headers `0`, and `c_status` = `A`.

`m_body` 는 A3ASD 가 해석하지 않는 본문이다.
`m_body` is an opaque body that A3ASD does not interpret.

- 캐릭터: CR 로 나뉜 `키=값` 목록(와이어에서는 CR 이 `\_1`). 예: `EXP=0\_1SKILL=1;2;3\_1PK=0\_1…`
  Character: a CR-separated `key=value` list (CR appears as `\_1` on the wire), e.g. `EXP=0\_1SKILL=1;2;3\_1PK=0\_1…`
- 창고: 키 없이 `;` 로 이어진 값 목록(`코드;info;키;칸;…`, 최대 80항목). 빈 창고는 `0;0;0;0`.
  Storage: a key-less `;`-joined value list (`code;info;key;slot;…`, up to 80 items). An empty storage is `0;0;0;0`.

### 실패 응답 / Failure replies

```
<reply> 0 <code> <text>\r\n
```

| 코드 / Code | 의미 / Meaning | 예 / Example |
|---|---|---|
| 101 | 행 없음(로드·저장·상태 변경) / 이미 있음(생성) / no row (load, save, set status) / already exists (create) | `3011 0 101 해당\_3Keyword가\_3없음`, `3002 0 101 Already\_3registered\_3ID.` |
| 901 | 인자 부족 / too few arguments | `3071 0 901 MSG\_3형식\_3오류` |
| 902 | DB 오류 / database error | `3011 0 902 Query\_3실행오류`, `3002 0 902 DB\_3Server\_3ERROR` |
| 903 | 생성 전 존재 확인 실패 / existence check before create failed | `3002 0 903 Critical\_3Error\_3for\_3DB_is_registered` |

성공 응답 / Success replies: `3002 1 0 OK`, `3071 1`, `3003 1`.

### 응답별 MainServer 동작 / MainServer behavior per reply

| 응답 / Reply | 캐릭터 연결 / Character link | 창고 연결 / Storage link |
|---|---|---|
| `3011 1` | 키 비교 후 접속 진행 / compares the key, continues login | 창고를 메모리에 올리고 전달 / loads storage into memory and sends it |
| `3011 0 101` | 캐릭터 없음 처리 / character not found | **`2002` 로 창고 생성 / creates the storage with `2002`** |
| `3011 0 901/902` | 접속 실패 / login fails | 창고 오류 / storage error |
| `3002 1` | 생성 완료 / created | 빈 창고 생성 / empty storage created |
| `3002 0 …` | 생성 실패 / create fails | 창고 오류 / storage error |
| `3003 1`, `3003 0 101` | 삭제 완료(101 도 성공처럼 처리) / deleted (101 also treated as success) | 해당 없음 / n/a |
| `3003 0 901/902` | 삭제 실패 / delete fails | 해당 없음 / n/a |
| `3071 1`, **`3071 0 …`** | **둘 다 무시 / both ignored** | **둘 다 무시 / both ignored** |

## 5. 흐름 제어와 장애 / Flow control and failures

- 연결마다 요청을 하나씩 보내고 응답을 기다린다. 응답은 대기열 맨 앞 요청과 opcode 로 맞춘다. 맞지 않으면 그 요청을 버린다.
  One request per link at a time. A reply is matched to the head of the queue by opcode; on a mismatch the request is dropped.
- 20초 동안 응답이 없으면 경고를 남기고, 30초가 넘으면 맨 앞 요청을 버리고 다시 접속한다. 버려진 요청의 결과는 호출한 쪽에 전달되지 않는다.
  After 20 seconds without a reply MainServer logs a warning; after 30 seconds it drops the head request and reconnects. The caller of a dropped request is never told.
- 연결이 끊겨도 대기열은 유지되고, 다시 붙으면 맨 앞 요청부터 보낸다.
  The queue survives a disconnect; after reconnecting MainServer resends from the head.
- **저장 실패(`3071 0 …`)는 MainServer 가 무시하고 로그도 남기지 않는다.** DB 장애 중의 저장분은 사라진다. 운영 중 저장 실패는 A3ASD 로그(`[ERROR] ... DB error op=2071`)와 상태 줄의 `DB FAIL` 로만 알 수 있다.
  **MainServer ignores save failures (`3071 0 …`) and does not log them.** Saves made during a DB outage are lost. The only signs are the A3ASD log (`[ERROR] ... DB error op=2071`) and `DB FAIL` in its status bar.
- 로드 응답의 `c_id` 가 요청 키와 대소문자까지 같지 않으면 캐릭터 접속이 응답 없이 멈춘다. DB collation 이 대소문자를 무시하면 다른 표기의 행이 돌아올 수 있으므로 같은 이름의 다른 표기를 만들지 않는다.
  If the `c_id` in a load reply differs from the request key, even only in case, the character login hangs. A case-insensitive collation can return a differently-cased row, so do not allow names that differ only in case.
- 지원하지 않는 요청에 대한 `3000` 응답은 MainServer 호출자에게 전달되지 않는다. A3ASD 는 이런 요청을 `[WARN] UNKNOWN opcode` 로 기록한다.
  A `3000` reply to an unsupported request never reaches the MainServer caller. A3ASD logs such requests as `[WARN] UNKNOWN opcode`.
