using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PadTas.App.Services;

/// <summary>
/// 起動診断ログ。起動しない/落ちる問題の切り分け用に、
/// 起動の各段階と未処理例外を %LocalAppData%\PadTas\logs\startup.log に残す。
/// ログ書き込み自体が失敗してもアプリは止めない。
/// </summary>
public static class CrashLog
{
    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PadTas", "logs");

    public static string LogFile => Path.Combine(LogDirectory, "startup.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* ログ失敗は無視 */ }
    }

    public static void LogException(string where, Exception ex)
        => Log($"{where}: {ex.GetType().FullName}: {ex.Message}\n{ex}");

    /// <summary>プロセス全体の未処理例外をログする (握りつぶさず、記録だけ行う)。</summary>
    public static void RegisterGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogException("AppDomain.UnhandledException", ex);
            else Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            LogException("UnobservedTaskException", e.Exception);
    }

    /// <summary>WPF の UI スレッド例外。ログして内容を画面に出し、可能なら継続する。</summary>
    public static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"エラーが発生しました。\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"詳細ログ: {LogFile}",
            "PadTAS エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
