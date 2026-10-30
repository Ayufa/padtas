using System.Windows;
using PadTas.App.Services;
using Velopack;

namespace PadTas.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.RegisterGlobalHandlers();
        CrashLog.Log($"=== PadTAS 起動開始 (args: {string.Join(' ', args)}) / " +
                     $"OS: {Environment.OSVersion} / .NET: {Environment.Version}");

        // 自動更新フックはアプリ本体より先に動かす必要がある (Velopack の作法)。
        // ここで例外が出てもアプリ本体の起動までは進める (起動不能の切り分けのため)。
        try
        {
            VelopackApp.Build().Run();
            CrashLog.Log("Velopack ブートストラップ完了");
        }
        catch (Exception ex)
        {
            CrashLog.LogException("Velopack ブートストラップで例外 (起動は続行)", ex);
        }

        try
        {
            var app = new App();
            app.DispatcherUnhandledException += CrashLog.OnDispatcherUnhandledException;
            app.InitializeComponent();
            CrashLog.Log("App 初期化完了");
            app.Run();
            CrashLog.Log("App 終了");
        }
        catch (Exception ex)
        {
            CrashLog.LogException("App.Run で例外", ex);
            MessageBox.Show(
                $"PadTAS を起動できませんでした。\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"詳細ログ: {CrashLog.LogFile}",
                "PadTAS 起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
