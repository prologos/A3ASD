using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;

namespace A3ASD;

// A3ASD 로그 창. ListView(VirtualMode) + 링버퍼로 고주파 로그도 버틴다.
// A3ASD log window. ListView(VirtualMode) + a ring buffer survive high-volume logging.
// 설계: 서버 로그 -> 스레드 안전 큐 -> UI 타이머가 배치로 링버퍼에 넣고 화면/파일에 반영.
// Design: server log -> thread-safe queue -> a UI timer batches it into the ring buffer, screen and file.
public sealed class MainForm : Form
{
    private readonly AsdServer _server;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    // 로그 링버퍼(최근 Cap 줄만 보존) / log ring buffer (keeps only the last Cap lines).
    private const int Cap = 20000;
    private readonly string[] _buf = new string[Cap];
    private int _count;   // 보존 줄 수 / lines held
    private int _head;    // 가장 오래된 줄의 인덱스 / index of the oldest line

    // 서버 스레드 -> UI 로 넘어오는 대기 큐 / queue from server threads to the UI.
    private readonly ConcurrentQueue<string> _incoming = new();

    private readonly ListView _list;
    private readonly Label _status;
    private readonly Label _dbLabel;   // DB 상태 표시(실패 시 빨간 배경) / DB state badge (red when failed)
    private int _dbShown = int.MinValue;
    private readonly Button _btnStart, _btnStop, _btnClear;
    private readonly CheckBox _autoScroll;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _logDir;

    public MainForm(AppConfig cfg)
    {
        _server = new AsdServer(cfg);
        // OnLog 는 서버 스레드에서 불린다 -> UI 를 직접 건드리지 말고 큐에만 넣는다(스레드 안전).
        // OnLog fires on server threads -> never touch UI here; just enqueue (thread-safe).
        _server.OnLog += s => _incoming.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {s}");
        _logDir = cfg.Log.Dir;
        try { Directory.CreateDirectory(_logDir); } catch { }

        // UI 문구는 영어로 통일, 폰트는 Consolas 12px 로 통일. 자식 컨트롤은 폼 폰트를 상속.
        // UI text is English-only and the font is Consolas 12px everywhere; children inherit the form font.
        Text = $"{cfg.Title} - Middleware   [DB {cfg.Db.Mode}: {cfg.Db.Server}/{cfg.Db.Database}]   {Path.GetFileName(cfg.FilePath)}";
        Font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        StartPosition = FormStartPosition.CenterScreen;
        try { using var ico = typeof(MainForm).Assembly.GetManifestResourceStream("A3ASD.asd.ico"); if (ico != null) Icon = new System.Drawing.Icon(ico); } catch { /* 아이콘 없으면 기본 / default icon if missing */ }

        // 상단 버튼 바: 글꼴 크기에 맞춰 자동 크기 / top button bar: auto-sized to the font
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(4) };
        _btnStart = new Button { Text = "Start", AutoSize = true, MinimumSize = new Size(110, 0), Padding = new Padding(6, 2, 6, 2) };
        _btnStop = new Button { Text = "Stop", AutoSize = true, MinimumSize = new Size(110, 0), Padding = new Padding(6, 2, 6, 2), Enabled = false };
        _btnClear = new Button { Text = "Clear", AutoSize = true, MinimumSize = new Size(110, 0), Padding = new Padding(6, 2, 6, 2) };
        _autoScroll = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
        top.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnClear, _autoScroll });

        // 하단 상태 줄: [DB 배지][포트 상태], 높이 = 글꼴 높이 + 여백
        // Bottom status line: [DB badge][port status], height = font height + padding
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Font.Height + 10, BorderStyle = BorderStyle.Fixed3D };
        _dbLabel = new Label { Dock = DockStyle.Left, AutoSize = false, Width = TextRenderer.MeasureText("DB FAIL", Font).Width + 24, TextAlign = ContentAlignment.MiddleCenter, Text = "DB ..." };
        _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "idle" };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_dbLabel);

        // 로그 리스트(가상화) / log list (virtualized)
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            GridLines = false,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro
        };
        _list.Columns.Add("log", 4000);
        // 보이는 줄만 요청된다 -> 링버퍼에서 인덱스로 공급 / only visible rows are requested -> serve from the ring buffer.
        // 오류=빨강, 경고=주황(태그 기준) / error=red, warn=orange (by tag)
        _list.RetrieveVirtualItem += (s, e) =>
        {
            string line = _buf[(_head + e.ItemIndex) % Cap] ?? "";
            var item = new ListViewItem(line);
            if (LogTag.IsError(line)) item.ForeColor = ErrorColor;
            else if (LogTag.IsWarn(line)) item.ForeColor = WarnColor;
            e.Item = item;
        };
        _list.Resize += (s, e) => { if (_list.Columns.Count > 0) _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - 4); };

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);

        _btnStart.Click += (s, e) => Start();
        _btnStop.Click += (s, e) => Stop();
        _btnClear.Click += (s, e) => { _count = 0; _head = 0; _list.VirtualListSize = 0; _list.Invalidate(); };

        // 로그 배치 주기(매 줄 갱신 금지 — 모아서 반영) / log flush interval (never per-line; batched).
        _timer = new System.Windows.Forms.Timer { Interval = 120 };
        _timer.Tick += (s, e) => Flush();
        _timer.Start();

        // 시작 창 폭 = 640px 고정(창 테두리 포함, 100% 배율 기준 — 고배율이면 같은 비율로 커짐). 안쪽 높이 = 로그 20줄 실측값 고정.
        // Start window width is fixed at 640px (outer, at 100% scale; scales with DPI). Client height = measured 20 log lines.
        ClientSize = new Size(ClientSize.Width, LogicalToDeviceUnits(StartClientHeight));
        Width = LogicalToDeviceUnits(StartWidth);
        MinimumSize = new Size(480, 300);

        Shown += (s, e) => Start();           // 열리면 자동 시작 / auto-start on open
        FormClosing += (s, e) => Stop();
    }

    // 포트 상태 문구: "Account:17770 ON 1/3"(ON=listen 중, 현재/누적 접속) / port status: ON=listening, active/total
    private static string StatusText(IEnumerable<(string Role, int Port, bool On, int Active, long Total)> ports) =>
        string.Join("   ", ports.Select(p => $"{p.Role}:{p.Port} {(p.On ? "ON" : "OFF")} {p.Active}/{p.Total}"));

    private const int StartWidth = 640;   // 시작 창 폭(px) / start window width (px)
    // 로그 20줄이 딱 맞는 창 안쪽 높이(글꼴을 바꾸면 다시 재야 함): 버튼 줄 43 + 로그창(줄 18×20 + 테두리 4) + 상태 줄 25.
    // Client height that fits exactly 20 log lines (re-measure if the font changes): top 43 + list (18x20 + border 4) + status 25.
    // 제목 표시줄은 Windows 버전마다 달라서 바깥 높이가 아닌 안쪽 높이로 고정 / client height, because the title bar differs per Windows version.
    private const int StartClientHeight = 432;

    private static readonly Color ErrorColor = Color.FromArgb(255, 85, 85);
    private static readonly Color WarnColor = Color.FromArgb(255, 176, 64);

    private bool Running => _runTask != null && !_runTask.IsCompleted;

    private void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _runTask = _server.RunAsync(_cts.Token);
    }

    private void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
    }

    // UI 스레드에서만 호출(타이머 Tick) / called only on the UI thread (timer Tick).
    private void Flush()
    {
        int n = 0;
        while (_incoming.TryDequeue(out var line))
        {
            _buf[(_head + _count) % Cap] = line;
            if (_count < Cap) _count++;
            else _head = (_head + 1) % Cap;     // 가득 차면 가장 오래된 줄을 밀어낸다 / drop the oldest when full
            AppendFile(line);
            if (++n >= 3000) break;             // 한 틱 처리 상한 / cap work per tick
        }
        if (n > 0)
        {
            _list.VirtualListSize = _count;
            if (_autoScroll.Checked && _count > 0) _list.EnsureVisible(_count - 1);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        // 실행/중지는 DB 배지(DB -)와 포트 ON/OFF 로 드러나므로 별도 문구 없음.
        // Running/stopped is shown by the DB badge ("DB -") and port ON/OFF, so no separate text.
        _status.Text = "  " + StatusText(_server.Stats.Select(st => (st.Role, st.Port, st.Listening, st.Active, st.Accepted)));

        // DB 배지: 바뀔 때만 다시 칠함 / DB badge: repaint only on change
        int db = Running ? _server.DbState : int.MinValue + 1;
        if (db != _dbShown)
        {
            _dbShown = db;
            (_dbLabel.Text, _dbLabel.BackColor, _dbLabel.ForeColor) = db switch
            {
                1 => ("DB OK", Color.FromArgb(46, 125, 50), Color.White),
                -1 => ("DB FAIL", Color.FromArgb(198, 40, 40), Color.White),
                0 => ("DB ...", SystemColors.Control, SystemColors.ControlText),
                _ => ("DB -", SystemColors.Control, SystemColors.ControlText),
            };
        }
    }

    // 파일 로그(일자별, append) / file log (daily, append).
    private StreamWriter? _fw;
    private string _fwDay = "";
    private void AppendFile(string line)
    {
        try
        {
            string day = DateTime.Now.ToString("yyyyMMdd");
            if (_fw == null || _fwDay != day)
            {
                _fw?.Dispose();
                _fw = new StreamWriter(Path.Combine(_logDir, $"A3ASD-{day}.log"), append: true) { AutoFlush = true };
                _fwDay = day;
            }
            _fw.WriteLine(line);
        }
        catch { /* 로그 파일 실패는 무시 / ignore file-log failures */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _fw?.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
