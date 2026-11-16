// A3ASD - H3DB(ASD) 미들웨어의 C# 재작성. BDE 없이 MSSQL 직결.
// A3ASD - C# rewrite of the H3DB(ASD) middleware. Connects straight to MSSQL, no BDE.
// 실행 = WinForms 로그 창 하나뿐.
// The WinForms log window is the only run mode.
using System.Windows.Forms;
using A3ASD;

internal static class Program
{
    // WinForms 는 STA 스레드를 요구한다 / WinForms requires an STA thread.
    [STAThread]
    private static int Main(string[] args)
    {
        // 인자: 첫 비옵션 토큰 = 설정 경로(생략 시 exe 폴더의 H3DB.ini, 상대경로도 exe 폴더 기준). '-' 로 시작하는 토큰은 무시.
        // Args: first non-option token = config path (default H3DB.ini next to the exe; relative = exe folder). Tokens starting with '-' are ignored.
        string? cfgPath = args.FirstOrDefault(a => !a.StartsWith('-'));

        AppConfig cfg;
        try { cfg = AppConfig.Load(cfgPath); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "A3ASD - config error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // GC 를 조용하게: 지속 저지연 모드 = 세대 2 전체 수집을 가능한 한 백그라운드로 미룬다.
        // 미들웨어 응답 지연의 흔들림을 줄이는 목적. 메모리 압박이 심하면 런타임이 스스로 해제한다.
        // Quiet GC: sustained low latency defers blocking gen-2 collections where possible, to steady reply latency.
        // The runtime lifts it on its own under memory pressure.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(cfg));
        return 0;
    }
}
