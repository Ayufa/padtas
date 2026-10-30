using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Nefarius.ViGEm.Client.Exceptions;
using PadTas.App.Services;
using PadTas.Core;

namespace PadTas.App;

public partial class MainWindow : Window
{
    private readonly RecorderService _recorder = new();
    private readonly PlaybackService _playback = new();
    private readonly DispatcherTimer _monitorTimer;
    private IVirtualGamepad? _virtualPad;
    private TasProject? _project;
    private long _lastPadUiUpdate; // コントローラー表示の更新間引き用

    public MainWindow()
    {
        CrashLog.Log("MainWindow 生成開始");
        InitializeComponent();
        CrashLog.Log("MainWindow XAML 読み込み完了");
        LoadIcon();

        // 記録中: 実入力をそのまま表示 (UI 更新は約30fpsに間引く)
        _recorder.LiveState += state => ShowStateThrottled(state);
        // 再生中: 送信している入力を表示
        _playback.StateSent += state => ShowStateThrottled(state);
        _playback.Finished += () => Dispatcher.BeginInvoke(() => PadView.Reset());

        // 待機中: 実コントローラーの入力モニター (記録も再生もしていないときだけ)
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _monitorTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording || _playback.IsPlaying) return;
            if (XInput.TryGetState(ControllerBox.SelectedIndex, out var state, out _))
                ShowState(state);
        };
        _monitorTimer.Start();

        StatusText.Text = $"バージョン: {UpdateService.CurrentVersion} / プロジェクトフォルダを選んでください。";
        Loaded += async (_, _) =>
        {
            CrashLog.Log("MainWindow 表示完了 (Loaded)");
            RefreshDriverButton();

            // 起動時の自動更新チェック (インストール版のみ・失敗しても起動は続行)
            try
            {
                var update = await UpdateService.CheckAsync();
                if (update is not null)
                    StatusText.Text = $"新しいバージョン {update.TargetFullRelease.Version} があります。" +
                                      "「アップデート確認」から適用できます。";
            }
            catch { /* オフライン等は無視 */ }
        };
    }

    /// <summary>
    /// アイコンは exe と同じフォルダの実ファイルから読み込む。
    /// XAML のリソース参照で読み込むと、埋め込みに失敗した環境で
    /// 起動自体が落ちる事故が起きた (v0.3.1 までの不具合) ため、
    /// ここは「失敗しても起動を止めない」読み込みに徹する。
    /// </summary>
    private void LoadIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (!File.Exists(iconPath))
            {
                CrashLog.Log($"アイコンファイルなし (スキップ): {iconPath}");
                return;
            }
            using var fs = File.OpenRead(iconPath);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            Icon = frame;
            HeaderIcon.Source = frame;
        }
        catch (Exception ex)
        {
            CrashLog.LogException("アイコン読み込み失敗 (無視して起動継続)", ex);
        }
    }

    // ---------- コントローラー表示 ----------

    private void ShowStateThrottled(GamepadState state)
    {
        var now = Environment.TickCount64;
        if (now - _lastPadUiUpdate < 33) return;
        _lastPadUiUpdate = now;
        Dispatcher.BeginInvoke(() => ShowState(state));
    }

    private void ShowState(GamepadState state)
    {
        PadView.SetState(state);
        LiveStateText.Text =
            $"Buttons: {state.Buttons:X4}   LX: {state.LeftX,6}  LY: {state.LeftY,6}   " +
            $"RX: {state.RightX,6}  RY: {state.RightY,6}   LT: {state.LeftTrigger,3}  RT: {state.RightTrigger,3}";
    }

    // ---------- プロジェクト ----------

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "プロジェクトフォルダ (project.json がある場所、または新規フォルダ)" };
        if (dialog.ShowDialog() != true) return;
        var folder = dialog.FolderName;
        try
        {
            _project = File.Exists(Path.Combine(folder, TasProject.ManifestFileName))
                ? TasProject.Load(folder)
                : new TasProject { FolderPath = folder };
            _project.SaveManifest();
            ProjectFolderBox.Text = folder;
            RefreshSegmentList();
            SetStatus($"プロジェクトを開きました: セグメント {_project.Segments.Count} 件");
        }
        catch (Exception ex) { SetStatus($"プロジェクトを開けませんでした: {ex.Message}"); }
    }

    private void RefreshSegmentList()
    {
        SegmentList.Items.Clear();
        if (_project is null) return;
        var i = 1;
        foreach (var s in _project.Segments)
            SegmentList.Items.Add($"{i++}. {s.Name}  ({s.DurationSeconds:F3} 秒)");
    }

    // ---------- 入力の削除 ----------

    private void DeleteSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { SetStatus("先にプロジェクトフォルダを選んでください。"); return; }
        var index = SegmentList.SelectedIndex;
        if (index < 0 || index >= _project.Segments.Count)
        { SetStatus("削除する入力をリストから選んでください。"); return; }

        var segment = _project.Segments[index];
        if (MessageBox.Show(
                $"「{segment.Name}」({segment.DurationSeconds:F3} 秒) を削除します。\n" +
                "記録ファイルも消え、元に戻せません。よろしいですか?",
                "入力の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _project.RemoveSegment(segment);
        RefreshSegmentList();
        SetStatus($"削除しました: {segment.Name} (残り {_project.Segments.Count} 件)");
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { SetStatus("先にプロジェクトフォルダを選んでください。"); return; }
        if (_project.Segments.Count == 0) { SetStatus("削除できる入力がありません。"); return; }
        if (MessageBox.Show(
                $"このプロジェクトの入力 {_project.Segments.Count} 件を全て削除します。\n" +
                "記録ファイルも消え、元に戻せません。よろしいですか?",
                "全ての入力を削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _project.RemoveAllSegments();
        RefreshSegmentList();
        SetStatus("全ての入力を削除しました。");
    }

    // ---------- 記録 ----------

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { SetStatus("先にプロジェクトフォルダを選んでください。"); return; }
        var polling = new[] { 250, 500, 1000, 2000 }[PollingBox.SelectedIndex];
        try
        {
            _recorder.StartRecording(ControllerBox.SelectedIndex, polling);
            RecordButton.IsEnabled = false;
            StopRecordButton.IsEnabled = true;
            SetStatus($"記録中… (セグメント {_project.Segments.Count + 1} 件目として末尾に追加されます)");
        }
        catch (Exception ex) { SetStatus($"記録を開始できませんでした: {ex.Message}"); }
    }

    private void StopRecord_Click(object sender, RoutedEventArgs e)
    {
        var recording = _recorder.StopRecording();
        RecordButton.IsEnabled = true;
        StopRecordButton.IsEnabled = false;
        if (_project is null) return;
        var segment = _project.AppendSegment(recording, $"テイク {_project.Segments.Count + 1}");
        RefreshSegmentList();
        SetStatus($"保存しました: {segment.FileName} / {recording.Frames.Count} フレーム / {segment.DurationSeconds:F3} 秒");
    }

    // ---------- 再生 ----------

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _project.Segments.Count == 0)
        { SetStatus("再生できるセグメントがありません。"); return; }
        try
        {
            _virtualPad ??= new ViGEmGamepad();
        }
        catch (VigemBusNotFoundException)
        {
            SetStatus("ViGEmBus ドライバが入っていません。");
            if (MessageBox.Show("再生には ViGEmBus ドライバが必要です。今すぐインストールしますか?",
                    "ドライバ未導入", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                await InstallDriverAsync();
            return;
        }
        catch (Exception ex)
        {
            SetStatus("仮想コントローラーを作れませんでした: " + ex.Message);
            return;
        }
        var merged = _project.MergeAll();
        _playback.Play(merged, _virtualPad);
        SetStatus($"再生中: 全 {_project.Segments.Count} セグメント / {merged.Frames.Count} フレーム / {merged.DurationSeconds:F3} 秒");
    }

    private void StopPlay_Click(object sender, RoutedEventArgs e)
    {
        _playback.Stop();
        SetStatus("再生を止めました。");
    }

    // ---------- ViGEmBus ドライバ ----------

    private void RefreshDriverButton()
    {
        var installed = ViGEmDriverService.IsInstalled();
        DriverButton.Content = installed ? "✓ ドライバ導入済み" : "ドライバをインストール";
        DriverButton.IsEnabled = !installed;
    }

    private async void DriverButton_Click(object sender, RoutedEventArgs e)
        => await InstallDriverAsync();

    private async Task InstallDriverAsync()
    {
        if (MessageBox.Show(
                "再生に必要な ViGEmBus ドライバ (仮想コントローラー用) をインストールします。\n" +
                "公式 GitHub からダウンロードし、照合してから実行します。\n" +
                "途中で Windows の管理者許可ダイアログが出たら「はい」を押してください。",
                "ドライバのインストール", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;

        DriverButton.IsEnabled = false;
        var result = await ViGEmDriverService.InstallAsync(
            msg => Dispatcher.BeginInvoke(() => SetStatus(msg)));
        RefreshDriverButton();
        SetStatus(result switch
        {
            ViGEmDriverService.InstallResult.Installed =>
                "ドライバをインストールしました。再生が使えます。",
            ViGEmDriverService.InstallResult.NeedsReboot =>
                "インストーラーは終了しましたが、ドライバを検出できませんでした。PCを再起動してからもう一度お試しください。",
            ViGEmDriverService.InstallResult.Cancelled =>
                "管理者許可が得られなかったため、ドライバのインストールを中止しました。",
            _ => "ドライバのインストールに失敗しました。通信環境を確認してもう一度お試しください。",
        });
    }

    // ---------- 更新 ----------

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!UpdateService.IsInstalled)
            { SetStatus("開発実行中 (未インストール) のため更新チェックはできません。配布版 Setup.exe からインストールしてください。"); return; }
            SetStatus("アップデートを確認中…");
            var update = await UpdateService.CheckAsync();
            if (update is null) { SetStatus("最新のバージョンです。"); return; }
            if (MessageBox.Show($"新しいバージョン {update.TargetFullRelease.Version} を適用して再起動しますか?",
                    "PadTAS アップデート", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await UpdateService.DownloadAndApplyAsync(update,
                p => Dispatcher.BeginInvoke(() => SetStatus($"ダウンロード中… {p}%")));
        }
        catch (Exception ex) { SetStatus($"アップデート確認に失敗しました: {ex.Message}"); }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    protected override void OnClosed(EventArgs e)
    {
        _monitorTimer.Stop();
        if (_recorder.IsRecording) _recorder.StopRecording();
        _playback.Stop();
        _virtualPad?.Dispose();
        base.OnClosed(e);
    }
}
