using System.Net;
using System.Net.Sockets;

namespace A3ASD;

// 로그 수준 태그(줄 앞에 붙임). 화면/콘솔은 이 태그로 색을 정한다. 파일 로그에도 그대로 남아 검색 가능.
// Log level tags (prefixed to the line). GUI/console colour by tag; kept in the file log for grepping.
public static class LogTag
{
    public const string Error = "[ERROR]";
    public const string Warn = "[WARN]";
    public static bool IsError(string line) => line.Contains(Error, StringComparison.Ordinal);
    public static bool IsWarn(string line) => line.Contains(Warn, StringComparison.Ordinal);
}

// ASD 서버 코어: 설정의 인스턴스(포트)마다 TCP 리스너를 돌린다. UI/콘솔과 분리된 재사용 단위.
// ASD server core: one TCP listener per configured instance (port). Reusable, decoupled from UI/console.
public sealed class AsdServer
{
    private readonly AppConfig _cfg;
    private const string PayRole = "Pay";

    // 로그 한 줄 발행(타임스탬프 없음 — 싱크가 붙인다) / emit one log line (no timestamp; the sink adds it).
    public event Action<string>? OnLog;

    // 포트별 상태(상태 패널용) / per-port stats for the status panel.
    public sealed class PortStat
    {
        public string Role = "";
        public int Port;
        public bool Listening;
        internal long _accepted;   // 누적 접속 수 / total accepted
        internal int _active;      // 현재 접속 수 / currently open
        public long Accepted => Interlocked.Read(ref _accepted);
        public int Active => _active;
    }

    private readonly List<PortStat> _stats = new();
    public IReadOnlyList<PortStat> Stats => _stats;   // RunAsync 전에 고정 생성(스레드에서 Add 안 함) / fixed up front, never Add from threads
    public AppConfig Config => _cfg;

    public AsdServer(AppConfig cfg)
    {
        _cfg = cfg;
        foreach (var inst in cfg.Instances)
            _stats.Add(new PortStat { Role = inst.Role, Port = inst.Port });
        if (cfg.Pay.Port > 0) _stats.Add(new PortStat { Role = PayRole, Port = cfg.Pay.Port });
    }

    private void Log(string m) => OnLog?.Invoke(m);

    // 빌드 시각(csproj SourceRevisionId 로 새김, "1.0.0+20261107.114500" 의 '+' 뒤). 로그를 받았을 때 어느 빌드인지 가리는 용도.
    // Build time stamped via csproj SourceRevisionId (the part after '+'); tells which build produced a log.
    public static readonly string BuildStamp = ReadBuildStamp();

    private static string ReadBuildStamp()
    {
        string? v = typeof(AsdServer).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return "unknown";
        int plus = v.IndexOf('+');
        return plus < 0 ? v : v[(plus + 1)..];
    }

    // DB 상태: 0=미확인, 1=정상, -1=실패. 시작 점검 + 매 요청 결과로 갱신, 상태가 바뀔 때만 로그(반복 스팸 방지).
    // DB state: 0=unknown, 1=OK, -1=failed. Updated by the startup ping and every request; logged only on change.
    private int _dbState;
    public int DbState => Volatile.Read(ref _dbState);

    // DB 결과 보고(스레드 안전) / report a DB result (thread-safe)
    internal void ReportDb(bool ok, string? error = null)
    {
        int next = ok ? 1 : -1;
        int prev = Interlocked.Exchange(ref _dbState, next);
        if (prev == next) return;
        if (ok) Log(prev == -1 ? "DB connection restored" : "DB connection OK");
        else Log($"{LogTag.Error} DB connection failed: {error} (game requests will get 902 until the DB is reachable)");
    }

    // 설정에 따라 저장소 생성 (mssql=실 DB, memory=자체 테스트) / create the store per config.
    private IRecordStore MakeStore(string table) =>
        string.Equals(_cfg.Db.Mode, "memory", StringComparison.OrdinalIgnoreCase)
            ? new InMemoryStore()
            : new SqlRecordStore(_cfg.Db.ConnectionString, table);

    // DB 연결 점검(한 번) / check DB connection once.
    public async Task<bool> PingAsync()
    {
        try
        {
            await MakeStore(_cfg.Instances[0].Table).PingAsync();
            ReportDb(true);
            return true;
        }
        catch (Exception ex)
        {
            ReportDb(false, ex.Message);
            return false;
        }
    }

    // 모든 인스턴스를 띄우고 취소될 때까지 돈다 / start all instances and run until cancelled.
    public async Task RunAsync(CancellationToken ct)
    {
        Volatile.Write(ref _dbState, 0);
        Log($"A3ASD start. build={BuildStamp}, ini={_cfg.FilePath}, DB={_cfg.Db.Server}/{_cfg.Db.Database}, auth={(string.Equals(_cfg.Db.Mode, "memory", StringComparison.OrdinalIgnoreCase) ? "-" : _cfg.Db.AuthLabel)}, instances={_cfg.Instances.Count}");
        _ = LogCleanupLoop(ct);   // 오래된 로그 파일 정리(시작 시 + 6시간마다) — 시작 로그 다음에 / prune old logs (now + every 6h), after the start line
        await PingAsync();

        var tasks = new List<Task>();
        var stores = new Dictionary<string, IRecordStore>(StringComparer.OrdinalIgnoreCase);   // 테이블별 저장소 / store per table
        for (int i = 0; i < _cfg.Instances.Count; i++)
        {
            var inst = _cfg.Instances[i];
            var stat = _stats[i];
            var store = MakeStore(inst.Table);
            stores[inst.Table] = store;
            var handler = new ProtocolHandler(store, inst, Log, _cfg.Log.Wire, _cfg.Log.WirePayload, ReportDb);
            tasks.Add(ListenAsync(inst, handler.ServeAsync, stat, ct));
        }
        if (_cfg.Pay.Port > 0)
        {
            // 접속 주체를 고정하지 않는다: 지금은 LoginServer 뿐이지만 다른 서버도 주기적으로 만료를 조회할 수 있다.
            // 누가 붙었는지는 연결 로그의 IP:포트로 구분한다.
            // Not tied to one client: only LoginServer today, but other servers may poll expiry later. Peers are told apart by IP:port in the connect log.
            var inst = new InstanceConfig { Port = _cfg.Pay.Port, Table = _cfg.Pay.Table, Role = PayRole, Client = "any" };
            var store = stores.TryGetValue(inst.Table, out var s) ? s : MakeStore(inst.Table);
            var handler = new PayHandler(store, inst, _cfg.Pay.Column, Log, _cfg.Log.Wire, ReportDb);
            tasks.Add(ListenAsync(inst, handler.ServeAsync, _stats[^1], ct));
        }
        await Task.WhenAll(tasks);
        Log("A3ASD stopped");
    }

    // 로그 정리 루프: [log] keepdays 일보다 오래된 A3ASD-yyyyMMdd.log 삭제. 0 이면 아무것도 안 함.
    // Log cleanup loop: delete A3ASD-yyyyMMdd.log older than [log] keepdays. 0 = do nothing.
    private async Task LogCleanupLoop(CancellationToken ct)
    {
        int days = _cfg.Log.KeepDays;
        if (days <= 0) { Log("log cleanup disabled (keepdays=0)"); return; }
        Log($"log cleanup: keep {days} days ({_cfg.Log.Dir})");
        while (!ct.IsCancellationRequested)
        {
            PruneLogs(_cfg.Log.Dir, days);
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { break; }
        }
    }

    // 파일 이름의 날짜 기준(수정 시각 아님). 이름이 정확히 A3ASD-yyyyMMdd.log 인 파일만 대상 — 다른 파일은 건드리지 않음.
    // Uses the date in the file name (not mtime). Only exact A3ASD-yyyyMMdd.log names are touched.
    internal void PruneLogs(string dir, int days)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            DateTime cutoff = DateTime.Today.AddDays(-days);   // 이 날짜보다 이전 파일 삭제 / delete files dated before this
            int deleted = 0;
            foreach (string path in Directory.EnumerateFiles(dir, "A3ASD-*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(path);   // A3ASD-yyyyMMdd
                if (name.Length != 14 || !DateTime.TryParseExact(name[6..], "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                    continue;
                if (d >= cutoff) continue;
                try { File.Delete(path); deleted++; }
                catch (Exception ex) { Log($"{LogTag.Warn} log cleanup: cannot delete {Path.GetFileName(path)}: {ex.Message}"); }
            }
            if (deleted > 0) Log($"log cleanup: deleted {deleted} file(s) older than {cutoff:yyyy-MM-dd}");
        }
        catch (Exception ex) { Log($"{LogTag.Warn} log cleanup failed: {ex.Message}"); }
    }

    // 한 포트에서 연결을 받아 핸들러에 넘긴다 / accept connections on one port and hand them to the handler.
    private async Task ListenAsync(InstanceConfig inst, Func<TcpClient, CancellationToken, Task> serve, PortStat stat, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, inst.Port);
        try { listener.Start(); stat.Listening = true; }
        catch (Exception ex) { Log($"{LogTag.Error} [{inst.Role}:{inst.Port}] listen failed: {ex.Message}"); return; }
        Log($"[{inst.Role}:{inst.Port}] listen started (table={inst.Table}, client={inst.Client})");

        using var reg = ct.Register(listener.Stop);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                Interlocked.Increment(ref stat._accepted);
                Interlocked.Increment(ref stat._active);
                _ = ServeWrap(serve, client, stat, ct);   // 연결마다 독립 처리 / handle each connection independently
            }
        }
        finally { stat.Listening = false; try { listener.Stop(); } catch { } }
    }

    // 접속 수 카운트를 위해 ServeAsync 를 감싼다 / wrap ServeAsync to keep the active-connection count.
    private static async Task ServeWrap(Func<TcpClient, CancellationToken, Task> serve, TcpClient client, PortStat stat, CancellationToken ct)
    {
        try { await serve(client, ct); }
        finally { Interlocked.Decrement(ref stat._active); }
    }
}
