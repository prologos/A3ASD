using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace A3ASD;

// H3DB 텍스트 프로토콜 처리 (docs/ 의 프로토콜 문서 참고).
// H3DB text protocol handler (see the protocol documents in docs/).
//  프레이밍: 한 줄=한 메시지, 공백 구분, 끝 \r\n. 요청 20xx -> 응답 30xx.
//  Framing: one line = one message, space-separated, ends with \r\n. Request 20xx -> response 30xx.
//  레코드 = c_id, c_sheadera~c, c_headera~c, [d_cdate,] d_udate, c_status, [m_body].
//  로드 포맷 2종: account(2021/3012)=m_body 없이 d_udate 1개 / full(2011/3011)=11컬럼.
//  Two load formats: account(2021/3012)=no m_body, single d_udate / full(2011/3011)=11 columns.
// 할당 설계: 수신 줄은 풀 버퍼 슬라이스를 ACP 로 1회 디코딩, 요청은 Span 토크나이저로 분해해
//  DB 파라미터가 될 문자열만 만들고, 응답은 문자열을 거치지 않고 풀 바이트 버퍼에 바로 인코딩한다.
// Allocation design: the received line is decoded once from a pooled buffer slice, requests are split with a span
//  tokenizer so only DB-parameter strings are created, and replies are encoded straight into a pooled byte buffer.
public sealed class ProtocolHandler
{
    private readonly IRecordStore _store;
    private readonly string _role;
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly bool _logWire;      // [log] wire — 요청/응답 요약 / per-message summary
    private readonly bool _logPayload;   // [log] payload — 본문 전체 / full payload
    private readonly bool _maskHeadera;  // 로그에서 c_headera 가림(계정 포트) / hide c_headera in logs (account port)
    private readonly Action<bool, string?> _dbReport;   // DB 성공/실패 보고(상태 줄용) / report DB ok/fail (status line)
    private readonly Encoding _enc;

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    // 응답 꼬리 메시지(CP949). 게임 서버는 code 만 쓰고 이 문구는 버리지만 원본 ASD 와 같은 바이트로 보낸다.
    // Response trailer messages (CP949). Game servers use only the code, but the bytes match the original ASD.
    // 꼬리 문구는 이스케이프 후 송신: \_→\_0, \r→\_1, \n→\_2, 공백→\_3.
    // Trailers are escaped before sending: \_→\_0, CR→\_1, LF→\_2, space→\_3.
    private static readonly string MsgNoRecord = Escape("해당 Keyword가 없음");   // 로드 101 / load 101
    private static readonly string MsgNoId = Escape("해당 ID가 없습니다.");        // 저장/상태 변경 101 / save, set-status 101
    private static readonly string MsgBadFormat = Escape("MSG 형식 오류");          // 인자 부족 901 / too few arguments 901
    // 902 꼬리(요청별) / 902 trailers per request
    private static readonly string MsgQueryErrSfi = Escape("Query 실행오류");    // 2011
    private static readonly string MsgQueryErrSsi = Escape("Query 실행 오류");   // 2021
    private static readonly string MsgDbServerErr = Escape("DB Server ERROR");  // 2002 INSERT 실패 / insert failed
    // 2071/2048 의 DB 오류 문구 / DB error trailer for 2071/2048
    private static readonly string MsgQueryFail = Escape("Query 실행오류");
    // 2002 전용 / create-only
    private static readonly string MsgAlreadyRegistered = Escape("Already registered ID.");               // 101
    private static readonly string MsgCriticalRegistered = Escape("Critical Error for DB_is_registered"); // 903

    private const string ZeroDate = "18991230.000000";   // Delphi TDateTime 0 = NULL 날짜 / NULL date
    private const string DateFormat = "yyyyMMdd.HHmmss";

    private static string QueryFailMsg(string op) => op switch
    {
        "2011" => MsgQueryErrSfi,
        "2021" => MsgQueryErrSsi,
        "2002" => MsgDbServerErr,
        _ => MsgQueryFail,
    };

    // 이스케이프(순서 중요 — 이스케이프 문자 자체를 먼저) / escape (order matters: the escape sequence itself first)
    private static string Escape(string s) =>
        s.Replace(@"\_", @"\_0").Replace("\r", @"\_1").Replace("\n", @"\_2").Replace(" ", @"\_3");

    // 요청 헤더 분해 결과(동기 구간에서 만들고 await 를 넘긴다 — 참조 2개 + int) / parsed request header
    private readonly record struct Request(string Op, string Resp, string CId, int RestStart);

    public ProtocolHandler(IRecordStore store, InstanceConfig inst, Action<string> log, bool logWire, bool logPayload, Action<bool, string?> dbReport)
    {
        _store = store; _role = inst.Role; _port = inst.Port; _log = log; _logWire = logWire; _logPayload = logPayload; _dbReport = dbReport;
        // 계정 테이블(account, account0~15 …) 포트는 로그에서 c_headera(비밀번호/해시)를 가린다.
        // On account-table ports (account, account0~15 …) c_headera (password/hash) is hidden in logs.
        _maskHeadera = inst.Table.StartsWith("account", StringComparison.OrdinalIgnoreCase);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 게임 서버와 같은 시스템 ANSI(한국어=949) / same system ANSI as game servers (Korean=949)
        uint cp = OperatingSystem.IsWindows() ? GetACP() : 949u;
        try { _enc = Encoding.GetEncoding((int)cp); } catch { _enc = Encoding.GetEncoding(949); }
    }

    public async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"[{_role}:{_port}] connect {ep}");
        // 연결당 버퍼 2개(수신·응답)를 풀에서 빌려 연결이 끝날 때 돌려준다 / two pooled buffers per connection, returned on close
        var w = new WireWriter(_enc);
        LineReader? reader = null;
        try
        {
            client.NoDelay = true;
            using var ns = client.GetStream();
            reader = new LineReader(ns);
            while (!ct.IsCancellationRequested)
            {
                if (await reader.ReadLineAsync(ct) < 0) break;   // 연결 종료 / connection closed
                string? line = Decode(reader.Line);
                if (line is null) continue;                       // 빈 줄 / empty line
                w.Reset();
                await HandleAsync(line, w);
                // 응답과 \r\n 은 반드시 한 번의 쓰기로 보낸다(나눠 보내면 일부 게임 서버가 멈출 수 있다).
                // The reply and its \r\n go out in a single write (splitting it can stall some game servers).
                await ns.WriteAsync(w.WrittenMemory, ct);
                await ns.FlushAsync(ct);
            }
        }
        // Stop(취소)으로 끊긴 건 정상 종료 — 경고 없이 close 만 남긴다 / cancelled by Stop = normal shutdown, no warning
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log($"{LogTag.Warn} [{_role}:{_port}] connection error {ep}: {ex.Message}"); }
        finally
        {
            _log($"[{_role}:{_port}] close {ep}");
            reader?.Dispose();
            w.Dispose();
            client.Dispose();
        }
    }

    // 종결자 잔여(\r\n)를 떼고 ACP 로 디코딩. 빈 줄이면 null. / strip stray CR/LF and decode as ACP; null for an empty line
    private string? Decode(ReadOnlyMemory<byte> raw)
    {
        ReadOnlySpan<byte> s = raw.Span.TrimEnd("\r\n"u8);
        return s.IsEmpty ? null : _enc.GetString(s);
    }

    // 요청 한 건 처리. 응답은 w 에 쓰고(CRLF 포함) 끝난다. / handle one request; the reply (with CRLF) ends up in w
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask HandleAsync(string line, WireWriter w)
    {
        Request req = ParseRequest(line);
        LogC2S(req, line);
        string tag;   // 로그 요약용 결과: OK 또는 코드(101/901/902/903/999) / result tag for the log summary
        try
        {
            tag = req.Op switch
            {
                "2011" => await Load(req, LoadFormat.Full, w),       // charac/storage 로드 / full load
                "2021" => await Load(req, LoadFormat.Account, w),    // account 로드 / account load
                "2071" => await Save(line, req, w),                  // 저장(UPDATE 만, 없으면 101) / save (UPDATE only)
                "2002" => await Create(line, req, w),                // 생성 / create
                "2048" => await SetStatus(line, req, w),             // 상태 변경(삭제=X, 행은 유지) / set c_status (soft delete)
                _ => Unknown(req, line, w),
            };
            if (req.Resp != "3000") _dbReport(true, null);   // DB 를 거친 요청이 성공 / a DB-backed request succeeded
        }
        // DB/쿼리 오류: 연결 유지하고 902 실패 응답 / DB error: keep the connection, return 902
        catch (Exception ex)
        {
            _log($"{LogTag.Error} [{_role}:{_port}] DB error op={req.Op} id={req.CId}: {ex.Message}");
            _dbReport(false, ex.Message);
            w.Reset();
            Fail(w, req.Resp, "902", QueryFailMsg(req.Op));
            tag = "902";
        }
        LogS2C(req, tag, w);
        w.AppendCrLf();
    }

    // "<op> <c_id> ..." 헤더 분해. op 는 알려진 값이면 상수 문자열(할당 없음). c_id 는 DB 파라미터라 문자열로 만든다.
    // Parse the "<op> <c_id> ..." header. Known ops map to constant strings (no allocation); c_id becomes a string (DB parameter).
    private static Request ParseRequest(string line)
    {
        int sp = line.IndexOf(' ');
        ReadOnlySpan<char> opSpan = sp < 0 ? line : line.AsSpan(0, sp);
        string op = opSpan switch
        {
            "2011" => "2011", "2021" => "2021", "2071" => "2071", "2002" => "2002", "2048" => "2048",
            _ => opSpan.ToString(),
        };
        int restStart = sp < 0 ? line.Length : sp + 1;
        int sp2 = line.IndexOf(' ', restStart);
        string cId = sp2 < 0 ? line[restStart..] : line[restStart..sp2];
        return new Request(op, ResponseCode(op), cId, restStart);
    }

    // 로드: 성공 "<resp> 1 <컬럼들>", 없음 "<resp> 0 101 해당 Keyword가 없음".
    // Load: success "<resp> 1 <cols>", missing "<resp> 0 101 ...".
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> Load(Request req, LoadFormat fmt, WireWriter w)
    {
        LoadedRecord? r = await _store.LoadAsync(fmt, req.CId);
        if (r is not { } rec) { Fail(w, req.Resp, "101", MsgNoRecord); return "101"; }
        RecordFields f = rec.Fields;
        bool full = fmt == LoadFormat.Full;
        // full: c_id sa sb sc ha hb hc d_cdate d_udate c_status m_body / account: c_id sa sb sc ha hb hc d_udate c_status
        w.Append(req.Resp).Append(" 1 ");
        Text(w, rec.CId).Append(' ');
        Text(w, f.SHeaderA).Append(' '); Text(w, f.SHeaderB).Append(' '); Text(w, f.SHeaderC).Append(' ');
        Text(w, f.HeaderA).Append(' '); Text(w, f.HeaderB).Append(' '); Text(w, f.HeaderC).Append(' ');
        if (full) Date(w, rec.CDate).Append(' ');
        Date(w, rec.UDate).Append(' ');
        Text(w, f.Status);
        if (full) { w.Append(' '); Text(w, f.Body); }
        return "OK";
    }

    // 저장 = UPDATE 전용(없는 행을 만들지 않는다). 와이어 "c_id sa sb sc ha hb hc status m_body".
    //  토큰 < 10(opcode 포함) → "3071 0 901 MSG\_3형식\_3오류" / 갱신 0행 → "3071 0 101 해당\_3ID가\_3없습니다." / 성공 "3071 1".
    // Save = UPDATE only (never inserts). <10 tokens -> 901, 0 rows -> 101, else "3071 1".
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> Save(string line, Request req, WireWriter w)
    {
        if (PieceCount(line, req.RestStart) < RecordFields.Count + 1) return BadFormat(req, line, w);
        RecordFields f = ParseFields(line, req.RestStart);
        int n = await _store.UpdateAsync(f, req.CId);
        if (n > 0) { w.Append(req.Resp).Append(" 1"); return "OK"; }
        Fail(w, req.Resp, "101", MsgNoId);
        return "101";
    }

    // 생성:
    //  토큰<10 → 901 MSG\_3형식\_3오류 / 존재 확인 실패 → 903 Critical… / 이미 있음 → 101 Already\_3registered\_3ID.
    //  INSERT 성공 → "3002 1 0 OK" / INSERT 실패 → 902 DB\_3Server\_3ERROR
    // Create: <10 tokens 901 / exists-check error 903 / exists 101 / ok / insert error 902.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> Create(string line, Request req, WireWriter w)
    {
        if (PieceCount(line, req.RestStart) < RecordFields.Count + 1) return BadFormat(req, line, w);
        RecordFields f = ParseFields(line, req.RestStart);
        bool exists;
        try { exists = await _store.ExistsAsync(req.CId); }
        catch (Exception ex)
        {
            _log($"{LogTag.Error} [{_role}:{_port}] DB error op=2002 (exists check) id={req.CId}: {ex.Message}");
            _dbReport(false, ex.Message);
            Fail(w, req.Resp, "903", MsgCriticalRegistered);
            return "903";
        }
        if (exists) { Fail(w, req.Resp, "101", MsgAlreadyRegistered); return "101"; }
        // INSERT 예외는 HandleAsync 의 catch 가 902 DB\_3Server\_3ERROR 로 응답 / insert exceptions -> 902 via HandleAsync
        bool ok = await _store.InsertNewAsync(f, req.CId);
        if (ok) { w.Append(req.Resp).Append(" 1 0 OK"); return "OK"; }
        Fail(w, req.Resp, "101", MsgAlreadyRegistered);   // 0행 = 동시 생성 경합 / 0 rows = concurrent create race
        return "101";
    }

    // 2048 = c_status 단일 컬럼 갱신. 행 DELETE 아님 — 캐릭터 삭제는 "2048 <c_id> X"(soft delete).
    // 2048 = single-column c_status update. NOT a row DELETE — character delete is "2048 <c_id> X".
    //  성공 "3003 1", d_udate 미변경. 토큰 < 3 → "3003 0 901 MSG\_3형식\_3오류" / 0행 → "3003 0 101 해당\_3ID가\_3없습니다.".
    //  Success "3003 1", d_udate untouched. <3 tokens -> 901; 0 rows -> 101.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> SetStatus(string line, Request req, WireWriter w)
    {
        if (PieceCount(line, req.RestStart) < 2) return BadFormat(req, line, w);
        string value = SecondPiece(line, req.RestStart);
        int n = await _store.UpdateColumnAsync("c_status", value, req.CId);
        if (n > 0) { w.Append(req.Resp).Append(" 1"); return "OK"; }
        Fail(w, req.Resp, "101", MsgNoId);
        return "101";
    }

    // ---- 토큰 처리(동기, Span) / token helpers (synchronous, span-based) ----

    // rest 의 공백 구분 조각 수 = rest.Split(' ').Length (빈 조각 포함) / piece count, identical to Split(' ').Length
    private static int PieceCount(string line, int restStart) => line.AsSpan(restStart).Count(' ') + 1;

    // rest 의 두 번째 조각(2048 의 값) / second piece of rest (the 2048 value)
    private static string SecondPiece(string line, int restStart)
    {
        int start = line.IndexOf(' ', restStart) + 1;   // PieceCount >= 2 이므로 존재 / exists because PieceCount >= 2
        int end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    // 와이어 저장 토큰 -> RecordFields. rest.Split(' ', 9) 와 동일: c_id 다음 7조각은 공백마다 자르고(빈 조각 허용),
    // 마지막 m_body 는 나머지 전체(공백 포함), 부족분은 null.
    // Wire save tokens -> RecordFields, identical to Split(' ', 9): 7 pieces after c_id, m_body = the remainder, missing -> null.
    private static RecordFields ParseFields(string line, int restStart)
    {
        var t = new SpaceTokenizer(line.AsSpan(restStart));
        t.TryNext(out _);   // c_id = Request.CId / already parsed
        string? sa = Next(ref t), sb = Next(ref t), sc = Next(ref t);
        string? ha = Next(ref t), hb = Next(ref t), hc = Next(ref t);
        string? status = Next(ref t);
        string? body = t.TryRemainder(out var rest) ? rest.ToString() : null;
        return new RecordFields(sa, sb, sc, ha, hb, hc, status, body);

        static string? Next(ref SpaceTokenizer t) => t.TryNext(out var tok) ? tok.ToString() : null;
    }

    // 요청 opcode -> 응답 opcode (불규칙: 2021->3012, 2048->3003) / request->response opcode (irregular)
    private static string ResponseCode(string op) => op switch
    {
        "2011" => "3011",   // charac/storage 로드 / full load
        "2021" => "3012",   // account 로드 / account load
        "2071" => "3071",   // 저장 / save
        "2002" => "3002",   // 생성 / create
        "2048" => "3003",   // c_status 갱신(soft delete) / set status
        _ => "3000",
    };

    // ---- 응답 조립 / reply building ----

    // "<resp> 0 <code> <msg>" / failure line
    private static void Fail(WireWriter w, string resp, string code, string msg) =>
        w.Append(resp).Append(" 0 ").Append(code).Append(' ').Append(msg);

    // 문자열 컬럼 -> 와이어 토큰: null/빈 -> "-" / text column -> wire token: null/empty -> "-"
    private static WireWriter Text(WireWriter w, string? s) => w.Append(string.IsNullOrEmpty(s) ? "-" : s);

    // 날짜 컬럼 -> yyyyMMdd.HHmmss, null -> 제로날짜 / date column -> yyyyMMdd.HHmmss, null -> zero date
    private static WireWriter Date(WireWriter w, DateTime? d)
    {
        if (d is not DateTime dt) return w.Append(ZeroDate);
        Span<char> buf = stackalloc char[DateFormat.Length];
        dt.TryFormat(buf, out int n, DateFormat, CultureInfo.InvariantCulture);
        return w.Append(buf[..n]);
    }

    private string Unknown(Request req, string line, WireWriter w)
    {
        // 지원하지 않는 요청은 원문(앞 300자)을 [WARN] 으로 남긴다 / unsupported requests are logged as [WARN] with the raw line
        _log($"{LogTag.Warn} [{_role}:{_port}] UNKNOWN opcode '{req.Op}' -> 3000 0 999, request: {Clip(MaskRequest(req, line))}");
        w.Append("3000 0 999 UNKNOWN_OP");
        return "999";
    }

    // 인자 부족(901). 게임 서버가 형식이 틀린 메시지를 보냈다는 뜻이라 드물고 중요하다 → [log] 설정과 무관하게 [WARN] + 원문(앞 300자).
    // Too few arguments (901): a caller sent a malformed message — rare and important, so always [WARN] with the raw request.
    private string BadFormat(Request req, string line, WireWriter w)
    {
        _log($"{LogTag.Warn} [{_role}:{_port}] bad format op={req.Op} id={req.CId} -> {req.Resp} 0 901, request: {Clip(MaskRequest(req, line))}");
        Fail(w, req.Resp, "901", MsgBadFormat);
        return "901";
    }

    // 로그용 원문 자르기(앞 300자) / clip a raw request for the log (first 300 chars)
    private static string Clip(string line) => line.Length > 300 ? string.Concat(line.AsSpan(0, 300), "...") : line;

    // ---- 로그 / logging ----
    // payload=1 이면 본문 전체, 아니면 wire=1 일 때만 요약 / payload=1 -> full line; else summary only if wire=1
    private void LogC2S(Request req, string line)
    {
        if (_logPayload) _log($"[{_role}:{_port}] C2S {MaskRequest(req, line)}");
        else if (_logWire) _log($"[{_role}:{_port}] C2S op={req.Op} id={req.CId}");
    }

    // 응답 요약: 응답을 다시 파싱하지 않고 처리 결과 태그를 그대로 쓴다 / reply summary uses the result tag, no re-parsing
    private void LogS2C(Request req, string tag, WireWriter w)
    {
        if (_logPayload) _log($"[{_role}:{_port}] S2C {MaskReply(w.DebugString())}");
        else if (_logWire) _log($"[{_role}:{_port}] S2C op={req.Resp} id={req.CId} result={tag}");
    }

    // ---- 로그 가림 / log masking ----
    // 계정 포트에서 c_headera 자리를 "<hidden>" 으로 바꾼다. 로그 전용이며 와이어 송수신은 그대로다.
    //  요청 2071/2002: "<op> <c_id> sa sb sc ha …" → 토큰 5. 그 밖의 알려진 요청(2021/2011/2048)엔 c_headera 가 없다.
    //  모르는 요청: 형식을 모르므로 c_id 뒤 전부를 가리고 토큰 수만 남긴다.
    //  응답 "3012 1 …"/"3011 1 <c_id> sa sb sc ha …" → 토큰 6. 실패 응답엔 c_headera 가 없다.
    // On account ports replace the c_headera slot with "<hidden>" (log only; wire traffic is unchanged).
    //  Requests 2071/2002: token 5. Other known requests have no c_headera.
    //  Unknown requests: format unknown, so everything after c_id is hidden and only the token count is kept.
    //  Replies "3012 1"/"3011 1": token 6. Failure replies carry no c_headera.
    private const string Hidden = "<hidden>";

    private string MaskRequest(Request req, string line)
    {
        if (!_maskHeadera) return line;
        switch (req.Op)
        {
            case "2071": case "2002": return MaskToken(line, 5);
            case "2011": case "2021": case "2048": return line;
        }
        int sp = line.IndexOf(' ', req.RestStart);
        if (sp < 0) return line;   // op + c_id 뿐 / op and c_id only
        int n = PieceCount(line, sp + 1);
        return $"{line.AsSpan(0, sp)} <hidden {n} token(s)>";
    }

    private string MaskReply(string reply)
    {
        if (!_maskHeadera) return reply;
        return reply.StartsWith("3012 1 ", StringComparison.Ordinal) || reply.StartsWith("3011 1 ", StringComparison.Ordinal)
            ? MaskToken(reply, 6) : reply;
    }

    // 공백 구분 index 번째(0부터) 토큰을 가린다. 없거나 "-"(NULL) 이면 그대로. CR/LF 는 보존.
    // Hide the index-th (0-based) space-separated token; unchanged if missing or "-" (NULL). CR/LF kept.
    private static string MaskToken(string s, int index)
    {
        int start = 0;
        for (int i = 0; i < index; i++)
        {
            int sp = s.IndexOf(' ', start);
            if (sp < 0) return s;
            start = sp + 1;
        }
        int end = s.IndexOf(' ', start);
        if (end < 0) { end = s.Length; while (end > start && (s[end - 1] == '\r' || s[end - 1] == '\n')) end--; }
        if (end == start || (end - start == 1 && s[start] == '-')) return s;
        return string.Concat(s.AsSpan(0, start), Hidden, s.AsSpan(end));
    }
}

// 공백 구분 토크나이저(빈 토큰 허용). ref struct 라 await 를 넘지 않는 동기 구간에서만 쓴다.
// Space tokenizer (empty tokens allowed). A ref struct: used only in synchronous code that never crosses an await.
internal ref struct SpaceTokenizer
{
    private readonly ReadOnlySpan<char> _s;
    private int _pos;
    private bool _end;

    public SpaceTokenizer(ReadOnlySpan<char> s) { _s = s; _pos = 0; _end = false; }

    // 다음 조각. 끝나면 false. / next piece; false when exhausted
    public bool TryNext(out ReadOnlySpan<char> token)
    {
        if (_end) { token = default; return false; }
        int i = _s[_pos..].IndexOf(' ');
        if (i < 0) { token = _s[_pos..]; _pos = _s.Length; _end = true; }
        else { token = _s.Slice(_pos, i); _pos += i + 1; }
        return true;
    }

    // 남은 전체(공백 포함). 이미 끝났으면 false. / everything left (spaces included); false if already exhausted
    public bool TryRemainder(out ReadOnlySpan<char> rest)
    {
        if (_end) { rest = default; return false; }
        rest = _s[_pos..]; _pos = _s.Length; _end = true;
        return true;
    }
}

// 응답 조립용 바이트 버퍼. ArrayPool 에서 빌려 연결 수명 동안 재사용하고, 문자열을 거치지 않고 ACP 로 바로 인코딩한다.
// Reply byte buffer. Rented from ArrayPool for the connection's lifetime; text is encoded straight to ACP bytes.
public sealed class WireWriter : IDisposable
{
    private readonly Encoding _enc;
    private byte[] _buf;
    private int _len;

    public WireWriter(Encoding enc, int initialSize = 4096)
    {
        _enc = enc;
        _buf = ArrayPool<byte>.Shared.Rent(initialSize);
    }

    public int Length => _len;
    public ReadOnlyMemory<byte> WrittenMemory => _buf.AsMemory(0, _len);
    public void Reset() => _len = 0;

    public WireWriter Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return this;
        Ensure(_enc.GetMaxByteCount(text.Length));
        _len += _enc.GetBytes(text, _buf.AsSpan(_len));
        return this;
    }

    public WireWriter Append(char c)
    {
        if (c >= 0x80) { Span<char> one = stackalloc char[1]; one[0] = c; return Append(one); }
        Ensure(1);
        _buf[_len++] = (byte)c;
        return this;
    }

    public WireWriter AppendCrLf()
    {
        Ensure(2);
        _buf[_len++] = (byte)'\r';
        _buf[_len++] = (byte)'\n';
        return this;
    }

    // payload 로그 전용(디버그) / for the payload log only (debug)
    public string DebugString() => _enc.GetString(_buf, 0, _len);

    private void Ensure(int more)
    {
        if (_len + more <= _buf.Length) return;
        var nb = ArrayPool<byte>.Shared.Rent(Math.Max(_buf.Length * 2, _len + more));
        Buffer.BlockCopy(_buf, 0, nb, 0, _len);
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = nb;
    }

    public void Dispose()
    {
        var b = _buf;
        _buf = Array.Empty<byte>();
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }
}

// TCP 스트림에서 \n 또는 \r 로 끝나는 줄을 읽는다(연속 \r\n 은 한 줄). 줄은 풀 버퍼의 슬라이스(Line)로 노출되며
// 다음 ReadLineAsync 전까지만 유효하다. 버퍼 경계에 걸친 줄만 앞으로 당겨 이어 붙인다(그 줄 1회 복사).
// Reads a line ended by \n or \r (a \r\n pair counts as one). The line is exposed as a slice (Line) of a pooled buffer,
// valid until the next ReadLineAsync. Only a line straddling the buffer end is compacted to the front (one copy of it).
public sealed class LineReader : IDisposable
{
    private const int InitialSize = 65536;
    private const int MaxLine = 1 << 20;   // 이보다 긴 줄은 연결 오류로 끊는다 / longer lines close the connection

    private readonly NetworkStream _ns;
    private byte[] _buf;
    private int _len, _pos;          // 채워진 길이, 다음 줄 시작 / filled length, start of the next line
    private int _lineStart, _lineLen;

    public LineReader(NetworkStream ns)
    {
        _ns = ns;
        _buf = ArrayPool<byte>.Shared.Rent(InitialSize);
    }

    // 마지막으로 읽은 줄(종결자 제외) / the last line read, without its terminator
    public ReadOnlyMemory<byte> Line => _buf.AsMemory(_lineStart, _lineLen);

    // 줄 길이(종결자 제외)를 돌려준다. EOF 면 -1. / returns the line length (no terminator), or -1 at EOF
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> ReadLineAsync(CancellationToken ct)
    {
        int scanFrom = _pos;
        while (true)
        {
            if (TryTake(scanFrom)) return _lineLen;
            scanFrom = _len;                        // 이미 훑은 구간은 다시 보지 않는다 / never rescan
            if (_pos > 0) { scanFrom -= _pos; Compact(); }
            if (_len == _buf.Length) Grow();
            int n = await _ns.ReadAsync(_buf.AsMemory(_len), ct);
            if (n <= 0)
            {
                // 종결자 없이 끝난 마지막 줄 / a final line with no terminator
                if (_len > _pos) { _lineStart = _pos; _lineLen = _len - _pos; _pos = _len; return _lineLen; }
                return -1;
            }
            _len += n;
        }
    }

    // scanFrom 부터 종결자를 찾아 줄을 확정한다 / find a terminator from scanFrom and fix the line
    private bool TryTake(int scanFrom)
    {
        int i = _buf.AsSpan(scanFrom, _len - scanFrom).IndexOfAny((byte)'\r', (byte)'\n');
        if (i < 0) return false;
        int end = scanFrom + i;
        _lineStart = _pos;
        _lineLen = end - _pos;
        byte b = _buf[end];
        int next = end + 1;
        if (next < _len && ((b == (byte)'\r' && _buf[next] == (byte)'\n') || (b == (byte)'\n' && _buf[next] == (byte)'\r')))
            next++;
        _pos = next;
        return true;
    }

    // 소비한 앞부분을 버리고 남은(미완성) 줄을 앞으로 당긴다 / drop consumed bytes, move the partial line to the front
    private void Compact()
    {
        Buffer.BlockCopy(_buf, _pos, _buf, 0, _len - _pos);
        _len -= _pos;
        _pos = 0;
    }

    private void Grow()
    {
        if (_buf.Length >= MaxLine) throw new InvalidDataException($"line longer than {MaxLine} bytes");
        var nb = ArrayPool<byte>.Shared.Rent(Math.Min(_buf.Length * 2, MaxLine));
        Buffer.BlockCopy(_buf, 0, nb, 0, _len);
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = nb;
    }

    public void Dispose()
    {
        var b = _buf;
        _buf = Array.Empty<byte>();
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }
}
