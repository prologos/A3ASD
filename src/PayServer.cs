using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace A3ASD;

// 계정 만료 확인 서버. 프로토콜은 LoginServer [PAYSERVER] 연결과 같고(docs/LoginServer-Pay.md),
// 같은 프로토콜로 다른 서버도 접속해 주기적으로 조회할 수 있다(접속 주체 제한 없음).
// Account expiry check server. Same protocol as the LoginServer [PAYSERVER] link; any other server may
// connect with the same protocol and poll periodically (no client restriction).
//  패킷 = 바이너리: +0 길이(u32, 자기 포함), +4 사용자 번호, +8 종류, +9 부종류.
//  Packets are binary: +0 length (u32, inclusive), +4 user number, +8 type, +9 subtype.
//  LoginServer 는 응답 시간 초과가 없어 응답을 빠뜨리면 그 사용자의 로그인이 멈춘다.
//  따라서 DB 오류·잘못된 값이어도 "만료 아님"으로 반드시 응답한다.
//  LoginServer has no reply timeout, so a missing reply hangs that login:
//  DB errors and bad values still answer "not expired".
//  요청은 로그인당 1회라 할당 민감도가 낮다. 연결당 수신·응답 버퍼만 재사용한다.
//  One request per login, so allocation matters little here; only the per-connection buffers are reused.
public sealed class PayHandler
{
    private const int HeaderSize = 10;
    private const int MaxPacket = 0x2000;      // LoginServer 쪽 수신 버퍼 크기 / LoginServer's receive buffer size
    private const int RequestSize = 0x2F;      // 요청 / request
    private const int ReplySize = 0x41;        // 응답 / reply
    private const byte TypeServer = 2;         // +8
    private const byte SubHello = 0xE0;        // 접속 인사·응답 / hello and reply
    private const byte SubCheck = 0xE1;        // 만료 확인 요청 / expiry check request

    private const int OffId = 0x0A, LenId = 21;    // 계정 ID / account ID
    private const int OffIp = 0x1F, LenIp = 16;    // 클라이언트 IP / client IP
    private const int OffDate = 0x2F;              // TDateTime(double)
    private const int OffResult = 0x37;            // Pascal 문자열 / Pascal string

    private const string ResultOk = "100";         // LoginServer 내부 응답과 같은 값 / same as LoginServer's internal reply
    private const string ResultExpired = "000";    // 이 값만 거부된다 / the only rejected value
    private static readonly DateTime MinOaDate = new(100, 1, 1);   // ToOADate 하한 / lowest value ToOADate accepts

    private readonly IRecordStore _store;
    private readonly string _role;
    private readonly int _port;
    private readonly string _column;
    private readonly Action<string> _log;
    private readonly bool _logWire;
    private readonly Action<bool, string?> _dbReport;
    private readonly Encoding _enc;
    private int _columnState;                      // 0=미확인, 1=있음, -1=없음 / 0=unknown, 1=present, -1=missing

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    // 판정 결과. Detail 이 null 이면 만료 시각이 있는 경우(로그에 expire=… 로 표시) / Detail null = an expiry time exists
    private readonly record struct Verdict(string Result, DateTime Date, string? Detail);

    public PayHandler(IRecordStore store, InstanceConfig inst, string column, Action<string> log, bool logWire, Action<bool, string?> dbReport)
    {
        _store = store; _role = inst.Role; _port = inst.Port; _column = column;
        _log = log; _logWire = logWire; _dbReport = dbReport;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        uint cp = OperatingSystem.IsWindows() ? GetACP() : 949u;
        try { _enc = Encoding.GetEncoding((int)cp); } catch { _enc = Encoding.GetEncoding(949); }
    }

    public async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"[{_role}:{_port}] connect {ep}");
        try
        {
            client.NoDelay = true;
            using var ns = client.GetStream();
            var buf = new byte[MaxPacket];       // 수신 / receive
            var reply = new byte[ReplySize];     // 응답(연결당 1개 재사용) / reply, reused per connection
            while (!ct.IsCancellationRequested)
            {
                await ns.ReadExactlyAsync(buf.AsMemory(0, 4), ct);
                int size = BitConverter.ToInt32(buf, 0);
                if (size < HeaderSize || size > MaxPacket)
                {
                    _log($"{LogTag.Warn} [{_role}:{_port}] bad packet size {size}, closing {ep}");
                    break;
                }
                await ns.ReadExactlyAsync(buf.AsMemory(4, size - 4), ct);

                if (await HandleAsync(buf, size, reply))
                {
                    await ns.WriteAsync(reply, ct);
                    await ns.FlushAsync(ct);
                }
            }
        }
        catch (EndOfStreamException) { }               // 상대가 끊음 / peer closed
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log($"{LogTag.Warn} [{_role}:{_port}] connection error {ep}: {ex.Message}"); }
        finally { _log($"[{_role}:{_port}] close {ep}"); client.Dispose(); }
    }

    // reply 를 채웠으면 true, 응답이 없는 패킷이면 false / true when reply was filled, false for packets with no reply
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> HandleAsync(byte[] p, int size, byte[] reply)
    {
        byte type = p[8], sub = p[9];
        if (type == TypeServer && sub == SubHello) return false;   // 접속 인사 / hello
        if (type != TypeServer || sub != SubCheck || size < RequestSize)
        {
            _log($"{LogTag.Warn} [{_role}:{_port}] unknown packet type={type} sub=0x{sub:X2} size={size}");
            return false;
        }

        string id = ReadField(p, OffId, LenId);
        Verdict v = await CheckAsync(id);
        if (_logWire) _log($"[{_role}:{_port}] id={id} {v.Detail ?? $"expire={v.Date:yyyy-MM-dd HH:mm:ss}"} result={v.Result}");
        BuildReply(p, v.Date, v.Result, reply);
        return true;
    }

    // 만료 판정. 어떤 실패도 예외로 내보내지 않는다 / expiry decision; never throws
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Verdict> CheckAsync(string id)
    {
        try
        {
            PayExpiry e = await _store.LoadExpiryAsync(_column, id);
            _dbReport(true, null);
            ReportColumn(e.ColumnExists);
            if (!e.ColumnExists) return new(ResultOk, e.Now, "no-column");
            if (e.Expire is not DateTime exp) return new(ResultOk, e.Now, "no-limit");
            return new(e.Now >= exp ? ResultExpired : ResultOk, exp, null);
        }
        catch (InvalidCastException ex)
        {
            _log($"{LogTag.Warn} [{_role}:{_port}] {_column} of '{id}' is not a date, treated as not expired: {ex.Message}");
            return new(ResultOk, DateTime.Now, "bad-value");
        }
        catch (Exception ex)
        {
            _log($"{LogTag.Error} [{_role}:{_port}] DB error id={id}, treated as not expired: {ex.Message}");
            _dbReport(false, ex.Message);
            return new(ResultOk, DateTime.Now, "db-error");
        }
    }

    // 컬럼 유무가 바뀔 때만 로그 / log only when column presence changes
    private void ReportColumn(bool exists)
    {
        int next = exists ? 1 : -1;
        if (Interlocked.Exchange(ref _columnState, next) == next) return;
        _log(exists
            ? $"[{_role}:{_port}] expiry column '{_column}' found"
            : $"{LogTag.Warn} [{_role}:{_port}] expiry column '{_column}' not found, all accounts are treated as not expired");
    }

    // NUL 종료 고정 길이 필드 -> 문자열(ACP) / NUL-terminated fixed field -> string (ACP)
    private string ReadField(byte[] p, int off, int max)
    {
        ReadOnlySpan<byte> field = p.AsSpan(off, max);
        int nul = field.IndexOf((byte)0);
        return _enc.GetString(nul < 0 ? field : field[..nul]);
    }

    private static void BuildReply(byte[] req, DateTime date, string result, byte[] r)
    {
        Array.Clear(r);
        BitConverter.TryWriteBytes(r.AsSpan(0), ReplySize);
        Array.Copy(req, 4, r, 4, 4);                       // 사용자 번호 / user number
        r[8] = TypeServer;
        r[9] = SubHello;
        Array.Copy(req, OffId, r, OffId, LenId + LenIp);   // ID + IP
        // 하한 미만이면 ToOADate 가 예외를 내 응답이 빠지므로 보정한다. date·datetime2 컬럼에서만 나올 수 있다.
        // Clamped because ToOADate throws below the limit and the reply would be lost. Only date/datetime2 columns can hold such values.
        double days = (date < MinOaDate ? MinOaDate : date).ToOADate();   // TDateTime 과 같은 기준 / same epoch as TDateTime
        BitConverter.TryWriteBytes(r.AsSpan(OffDate), days);
        r[OffResult] = (byte)result.Length;
        Encoding.ASCII.GetBytes(result, 0, result.Length, r, OffResult + 1);
    }
}
