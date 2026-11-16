using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.SqlClient;

namespace A3ASD;

// 설정 = exe 폴더의 H3DB.ini (원본 ASD 의 tdb.ini 와 같은 섹션/키=값 스타일).
// Config = H3DB.ini next to the exe (same section/key=value style as the original ASD tdb.ini).
//  인코딩: 시스템 ANSI(한국어=CP949, 원본 ini 와 동일). UTF-8 BOM 이 있으면 UTF-8 로 읽는다.
//  Encoding: system ANSI (Korean=CP949, like the original ini). A UTF-8 BOM switches to UTF-8.
//  상대경로(설정 파일·로그 폴더)는 항상 exe 폴더 기준 — 바로가기 "시작 위치"와 무관.
//  Relative paths (config file, log dir) always resolve against the exe folder.

// DB 접속 설정 / database connection settings
public sealed class DbConfig
{
    public string Mode { get; init; } = "mssql";   // mssql | memory (memory=DB 없이 자체 테스트 / self-test without DB)
    public string Server { get; init; } = "127.0.0.1";
    public string Database { get; init; } = "A3";
    public string User { get; init; } = "";      // 비우면 Windows 인증 / empty = Windows authentication
    public string Password { get; init; } = "";
    public string ExtraConnString { get; init; } = "TrustServerCertificate=True;Encrypt=False;";

    // user 가 비어 있으면 Windows 인증(실행 중인 Windows 계정으로 로그인, password 무시).
    // Empty user = Windows authentication (logs in as the running Windows account; password ignored).
    public bool WindowsAuth => string.IsNullOrWhiteSpace(User);
    public string AuthLabel => WindowsAuth ? $@"Windows({Environment.UserDomainName}\{Environment.UserName})" : $"SQL({User})";

    // SqlClient 연결 문자열. 비번에 ; 등이 있어도 안전하도록 빌더 사용.
    // SqlClient connection string; builder keeps passwords with ';' etc. safe.
    public string ConnectionString
    {
        get
        {
            var b = new SqlConnectionStringBuilder(ExtraConnString) { DataSource = Server, InitialCatalog = Database };
            if (WindowsAuth) b.IntegratedSecurity = true;
            else { b.UserID = User; b.Password = Password; }
            return b.ConnectionString;
        }
    }
}

// 인스턴스(포트) 설정: 포트 하나 = 테이블 하나 / one instance = one port bound to one table
public sealed class InstanceConfig
{
    public int Port { get; init; }
    public string Table { get; init; } = "";    // 이 포트가 쓰는 테이블 / table used by this port
    public string Role { get; init; } = "";      // 로그 표시용 / for logging only
    public string Client { get; init; } = "";    // 붙는 게임 서버(문서용) / which game server connects (doc)
}

// 계정 만료 확인 설정(LoginServer [PAYSERVER] 및 같은 프로토콜을 쓰는 모든 서버). port=0 이면 끔.
// Account expiry check settings (LoginServer [PAYSERVER] and any server using the same protocol). port=0 = off.
public sealed class PayConfig
{
    public int Port { get; init; }
    public string Table { get; init; } = "account";
    public string Column { get; init; } = "d_edate";
}

// 로그 설정 / logging settings
public sealed class LogConfig
{
    public bool Wire { get; init; } = true;             // 요청/응답 한 줄 요약 / per-message summary
    public bool WirePayload { get; init; } = false;     // 본문까지 로그(대용량·민감) / log full payload (large/sensitive)
    public string Dir { get; init; } = "log";           // exe 폴더 기준 절대경로로 변환됨 / resolved against exe folder
    public int KeepDays { get; init; } = 30;            // 로그 파일 보관 일수, 0=삭제 안 함 / days to keep log files, 0 = keep forever
}

// 전체 설정 / whole configuration
public sealed class AppConfig
{
    public const string DefaultFileName = "H3DB.ini";

    public string FilePath { get; init; } = "";          // 실제 읽은 설정 파일 / config file actually loaded
    public string Title { get; init; } = "A3ASD";
    public DbConfig Db { get; init; } = new();
    public List<InstanceConfig> Instances { get; init; } = new();
    public int KeepAliveSec { get; init; } = 10;
    public LogConfig Log { get; init; } = new();
    public PayConfig Pay { get; init; } = new();

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    // exe 폴더 기준 경로 / path relative to the exe folder
    public static string FromExeDir(string path) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    // H3DB.ini 로드. path 생략 시 exe 폴더의 H3DB.ini. / Load H3DB.ini (default: next to the exe).
    //  [main]   title=, keepalive=
    //  [db]     mode=, server=, database=, user=, password=, extra=
    //  [socket] NN=<port> <table> [role] [client]   (원본 tdb.ini 의 [dbconnection] NN= 줄 형식과 유사)
    //  [log]    wire=, payload=, dir=
    //  [pay]    port=, table=, column=
    public static AppConfig Load(string? path = null)
    {
        string file = FromExeDir(string.IsNullOrEmpty(path) ? DefaultFileName : path);
        if (!File.Exists(file))
            throw new FileNotFoundException($"config file not found: {file}");

        var ini = ReadIni(file);
        string Get(string sec, string key, string def) =>
            ini.TryGetValue(sec, out var s) && s.TryGetValue(key, out var v) ? v : def;
        int GetInt(string sec, string key, int def) =>
            int.TryParse(Get(sec, key, ""), out int n) ? n : def;
        bool GetBool(string sec, string key, bool def) => Get(sec, key, "").ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => def,
        };

        // [socket] NN=<port> <table> [role] [client] — 키 순서(00,01,..)대로 / in key order
        var instances = new List<InstanceConfig>();
        if (ini.TryGetValue("socket", out var sock))
        {
            foreach (var kv in sock.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string[] p = kv.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !int.TryParse(p[0], out int port) || port is <= 0 or > 65535)
                    throw new FormatException($"{file}: [socket] {kv.Key}={kv.Value} - expected '<port> <table> [role] [client]'");
                instances.Add(new InstanceConfig
                {
                    Port = port, Table = p[1],
                    Role = p.Length > 2 ? p[2] : p[1],
                    Client = p.Length > 3 ? p[3] : "",
                });
            }
        }
        if (instances.Count == 0)
            throw new InvalidOperationException($"{file}: no ports in [socket].");

        return new AppConfig
        {
            FilePath = file,
            Title = Get("main", "title", "A3ASD"),
            KeepAliveSec = GetInt("main", "keepalive", 10),
            Db = new DbConfig
            {
                Mode = Get("db", "mode", "mssql"),
                Server = Get("db", "server", "127.0.0.1"),
                Database = Get("db", "database", "A3"),
                User = Get("db", "user", ""),        // 없거나 비면 Windows 인증 / missing or empty = Windows auth
                Password = Get("db", "password", ""),
                ExtraConnString = Get("db", "extra", "TrustServerCertificate=True;Encrypt=False;"),
            },
            Instances = instances,
            Pay = new PayConfig
            {
                Port = GetInt("pay", "port", 0),
                Table = Get("pay", "table", "account"),
                Column = Get("pay", "column", "d_edate"),
            },
            Log = new LogConfig
            {
                Wire = GetBool("log", "wire", true),
                WirePayload = GetBool("log", "payload", false),
                Dir = FromExeDir(Get("log", "dir", "log")),
                KeepDays = Math.Max(0, GetInt("log", "keepdays", 30)),
            },
        };
    }

    // 단순 INI 파서: [섹션], 키=값(첫 '=' 기준), 줄 시작 ';'/'#' 는 주석. 섹션·키는 대소문자 무시.
    // Minimal INI parser: [section], key=value (split at first '='), ';'/'#' at line start = comment. Case-insensitive.
    private static Dictionary<string, Dictionary<string, string>> ReadIni(string file)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi;
        try { ansi = Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)GetACP() : 949); }
        catch { ansi = Encoding.GetEncoding(949); }

        var ini = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ini[""] = cur;
        using var rd = new StreamReader(file, ansi, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = rd.ReadLine()) is not null)
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] is ';' or '#') continue;
            if (t[0] == '[' && t[^1] == ']')
            {
                string name = t[1..^1].Trim();
                if (!ini.TryGetValue(name, out cur!)) ini[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            cur[t[..eq].Trim()] = t[(eq + 1)..].Trim();
        }
        return ini;
    }
}
