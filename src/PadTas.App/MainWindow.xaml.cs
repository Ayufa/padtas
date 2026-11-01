using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private PlaybackVerifier? _verifier;
    private Recording? _verifyingRecording;
    private CancellationTokenSource? _startCts; // 遅延/入力待ち/画面トリガーの待機中止用
    private (int X, int Y, (byte R, byte G, byte B) Color)? _triggerPixel; // 画面トリガーの基準色
    private readonly DispatcherTimer _monitorTimer;
    private IVirtualGamepad? _virtualPad;
    private TasProject? _project;
    private readonly object _uiGate = new();
    private readonly Action _applyLiveState;
    private GamepadState _uiPending;
    private int _uiQueued;
    private bool _hasPaintedState;
    private GamepadState _paintedState;

    public MainWindow()
    {
        _applyLiveState = ApplyLiveState;
        CrashLog.Log("MainWindow 生成開始");
        InitializeComponent();
        CrashLog.Log("MainWindow XAML 読み込み完了");
        LoadIcon();

        // 記録中: 実入力をそのまま表示 (送出側が約30fpsに間引く)
        _recorder.LiveState += ShowStateThrottled;
        // 再生中: 送信している入力を表示
        _playback.StateSent += ShowStateThrottled;
        // 再生の読み戻し検証: 開始時刻を受けて仮想コントローラーを監視し、
        // 終了後に「送った状態」と「実際に読めた状態」を照合して報告する
        _playback.Started += startQpc =>
        {
            if (_verifyingRecording is null) return;
            _verifier = new PlaybackVerifier();
            _verifier.Begin(startQpc, _verifyingRecording);
        };
        _playback.Finished += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _hasPaintedState = false;
                PadView.Reset();
            });
            var verifier = _verifier;
            _verifier = null;
            _verifyingRecording = null;
            if (verifier is null) return;
            _ = Task.Run(() =>
            {
                var result = verifier.StopAndEvaluate();
                CrashLog.Log("再生検証: " + result.Detail);
                Dispatcher.BeginInvoke(() => SetStatus(
                    "再生検証: " + result.Summary +
                    (result.Summary.Contains("不一致")
                        ? " ※ゲームが実コントローラーと仮想の両方を見ていると入力が競合します。実機の電源を切って再試行してみてください。"
                        : "")));
            });
        };

        // 待機中: 実コントローラーの入力モニター (記録も再生もしていないときだけ)
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _monitorTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording || _playback.IsPlaying) return;
            if (XInput.TryGetState(ControllerBox.SelectedIndex, out var state, out _))
                ShowState(state);
        };
        _monitorTimer.Start();

        Loaded += (_, _) =>
        {
            CrashLog.Log("MainWindow 表示完了 (Loaded)");
            _ = Task.Run(ShortcutService.EnsureStartMenuShortcut);
            // ドライバ接続とバージョン照会はウィンドウ表示をブロックしない
            _ = Task.Run(() =>
            {
                var installed = false;
                try { installed = ViGEmDriverService.IsInstalled(); }
                catch { /* 表示は未導入のまま */ }
                Dispatcher.BeginInvoke(() =>
                {
                    // ユーザーがすでにインストールを始めていたら、その操作を上書きしない
                    if ((DriverButton.Content as string) == "ドライバ確認中…" && DriverButton.IsEnabled)
                        ApplyDriverButton(installed);
                });
            });
            _ = Task.Run(async () =>
            {
                string? versionText = null;
                try
                {
                    versionText = $"バージョン: {UpdateService.CurrentVersion} / プロジェクトフォルダを選んでください。";
                }
                catch { /* 開発実行では dev に落ちるが、ここでも起動は止めない */ }

                if (versionText is not null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (StatusText.Text == "プロジェクトフォルダを選んでください。")
                            StatusText.Text = versionText;
                    });
                }

                // 起動時の自動更新チェック (インストール版のみ・失敗しても起動は続行)
                try
                {
                    var update = await UpdateService.CheckAsync();
                    if (update is not null)
                    {
                        await Dispatcher.InvokeAsync(() => StatusText.Text =
                            $"新しいバージョン {update.TargetFullRelease.Version} があります。" +
                            "「アップデート確認」から適用できます。");
                    }
                }
                catch { /* オフライン等は無視 */ }
            });
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
        bool schedule;
        lock (_uiGate)
        {
            _uiPending = state;
            schedule = _uiQueued == 0;
            _uiQueued = 1;
        }
        if (schedule) Dispatcher.BeginInvoke(_applyLiveState);
    }

    private void ApplyLiveState()
    {
        GamepadState state;
        lock (_uiGate)
        {
            state = _uiPending;
            _uiQueued = 0;
        }
        ShowState(state);
    }

    private void ShowState(GamepadState state)
    {
        if (_hasPaintedState && state == _paintedState) return;
        _hasPaintedState = true;
        _paintedState = state;
        PadView.SetState(state);
        LiveStateText.Text = FormatLiveState(state);
    }

    private const int LiveStateLength = 82;

    private static string FormatLiveState(GamepadState state)
    {
        return string.Create(LiveStateLength, state, static (dst, s) =>
        {
            var i = 0;
            Append(dst, ref i, "Buttons: ");
            AppendHex4(dst, ref i, s.Buttons);
            Append(dst, ref i, "   LX: ");
            AppendInt(dst, ref i, s.LeftX, 6);
            Append(dst, ref i, "  LY: ");
            AppendInt(dst, ref i, s.LeftY, 6);
            Append(dst, ref i, "   RX: ");
            AppendInt(dst, ref i, s.RightX, 6);
            Append(dst, ref i, "  RY: ");
            AppendInt(dst, ref i, s.RightY, 6);
            Append(dst, ref i, "   LT: ");
            AppendInt(dst, ref i, s.LeftTrigger, 3);
            Append(dst, ref i, "  RT: ");
            AppendInt(dst, ref i, s.RightTrigger, 3);
        });
    }

    private static void Append(Span<char> dst, ref int i, string text)
    {
        text.AsSpan().CopyTo(dst[i..]);
        i += text.Length;
    }

    private static void AppendHex4(Span<char> dst, ref int i, ushort value)
    {
        value.TryFormat(dst[i..], out _, "X4");
        i += 4;
    }

    private static void AppendInt(Span<char> dst, ref int i, int value, int width)
    {
        Span<char> tmp = stackalloc char[8];
        value.TryFormat(tmp, out var written);
        var pad = width - written;
        if (pad > 0) dst.Slice(i, pad).Fill(' ');
        tmp[..written].CopyTo(dst[(i + (pad > 0 ? pad : 0))..]);
        i += width;
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
        var polling = new[] { 250, 500, 1000, 2000, 4000, 8000 }[PollingBox.SelectedIndex];
        try
        {
            _recorder.StartRecording(ControllerBox.SelectedIndex, polling);
            RecordButton.IsEnabled = false;
            StopRecordButton.IsEnabled = true;
            SetStatus($"記録中… (セグメント {_project.Segments.Count + 1} 件目として末尾に追加されます)" +
                      (polling >= 4000
                          ? $" ※{polling} Hz は CPU を 1 コア占有します。実際に取れる細かさはコントローラー自体のポーリングレートが上限です。"
                          : ""));
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
        if (_playback.IsPlaying || _startCts is not null) return;
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
        var mode = StartModeBox.SelectedIndex;
        if (mode == 0)
        {
            BeginPlayback(merged);
            return;
        }
        if (mode == 5 && _triggerPixel is null)
        { SetStatus("画面トリガーには先に「画面トリガー用の色を拾う」で、ロード中などに色が変わる場所を指定してください。"); return; }

        // 待機系 (遅延 / 入力待ち / 画面トリガー): 停止ボタンで中止できる
        _startCts = new CancellationTokenSource();
        var ct = _startCts.Token;
        PlayButton.IsEnabled = false;
        try
        {
            var ready = mode switch
            {
                1 or 2 or 3 => await WaitSecondsAsync(mode switch { 1 => 3, 2 => 5, _ => 10 }, ct),
                4 => await WaitForButtonAsync(ct),
                5 => await WaitForPixelChangeAsync(ct),
                _ => false,
            };
            if (ready && !ct.IsCancellationRequested) BeginPlayback(merged);
            else if (!ct.IsCancellationRequested) SetStatus("開始の合図が来ないままタイムアウトしました。");
        }
        catch (OperationCanceledException) { /* 停止ボタン */ }
        finally
        {
            _startCts?.Dispose();
            _startCts = null;
            PlayButton.IsEnabled = true;
        }
    }

    private void BeginPlayback(Recording merged)
    {
        _verifyingRecording = merged; // Started イベントで読み戻し検証が始まる
        _playback.Play(merged, _virtualPad!);
        SetStatus($"再生中: 全 {_project!.Segments.Count} セグメント / {merged.Frames.Count} フレーム / {merged.DurationSeconds:F3} 秒");
    }

    private async Task<bool> WaitSecondsAsync(int seconds, CancellationToken ct)
    {
        SetStatus($"{seconds} 秒後に再生を開始します… (中止は再生停止ボタン)");
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return true;
    }

    /// <summary>入力待ち: 実コントローラーのボタンが押された瞬間を開始の合図にする。
    /// ゲーム側の操作 (ステージ開始ボタン等) と再生開始を同期させる用途。</summary>
    private async Task<bool> WaitForButtonAsync(CancellationToken ct)
    {
        SetStatus("入力待ち: コントローラーのボタンを押すとその瞬間に再生を開始します… (中止は再生停止ボタン)");
        var userIndex = ControllerBox.SelectedIndex;
        return await Task.Run(() =>
        {
            PreciseWait.AcquireTimerResolution();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (XInput.TryGetState(userIndex, out var state, out _) &&
                        (state.Buttons != 0 || state.LeftTrigger > 30 || state.RightTrigger > 30))
                        return true;
                    Thread.Sleep(1);
                }
                return false;
            }
            finally { PreciseWait.ReleaseTimerResolution(); }
        }, ct);
    }

    /// <summary>画面トリガー: 指定した 1 ピクセルの色が基準から変わった瞬間に開始。
    /// ロード画面 (真っ黒) → 画面表示 のような変化を合図にすると、
    /// ロード時間のばらつきを吸収して毎回同じタイミングで始められる。</summary>
    private async Task<bool> WaitForPixelChangeAsync(CancellationToken ct)
    {
        var (x, y, baseline) = _triggerPixel!.Value;
        SetStatus($"画面トリガー待機中: ({x}, {y}) の色が変わると再生を開始します… (中止は再生停止ボタン、制限 120 秒)");
        return await Task.Run(() =>
        {
            PreciseWait.AcquireTimerResolution();
            try
            {
                var deadline = Environment.TickCount64 + 120_000;
                while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
                {
                    var sample = ScreenPixel.Sample(x, y);
                    if (sample is not null && ScreenPixel.Difference(sample.Value, baseline) > 60)
                        return true;
                    Thread.Sleep(2);
                }
                return false;
            }
            finally { PreciseWait.ReleaseTimerResolution(); }
        }, ct);
    }

    private async void PickPixel_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("3 秒後にマウスカーソルのある場所の色を拾います。その場所 (ロード中に色が変わる場所) にカーソルを合わせて待ってください…");
        await Task.Delay(3000);
        if (!ScreenPixel.GetCursorPos(out var point))
        { SetStatus("カーソル位置を取得できませんでした。"); return; }
        var sample = ScreenPixel.Sample(point.X, point.Y);
        if (sample is null)
        { SetStatus("その場所の色を取得できませんでした。別の場所でもう一度お試しください。"); return; }
        _triggerPixel = (point.X, point.Y, sample.Value);
        PixelSwatch.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(sample.Value.R, sample.Value.G, sample.Value.B));
        PixelInfoText.Text = $"({point.X}, {point.Y}) RGB({sample.Value.R}, {sample.Value.G}, {sample.Value.B})";
        SetStatus($"画面トリガーの基準色を設定しました: ({point.X}, {point.Y}) RGB({sample.Value.R}, {sample.Value.G}, {sample.Value.B})。" +
                  "開始方法で「画面の色が変わったら開始」を選ぶと、この色が変わった瞬間に再生が始まります。");
    }

    private void StopPlay_Click(object sender, RoutedEventArgs e)
    {
        _startCts?.Cancel();
        _playback.Stop();
        SetStatus("再生を止めました。");
    }

    // ---------- ViGEmBus ドライバ ----------

    private void RefreshDriverButton() => ApplyDriverButton(ViGEmDriverService.IsInstalled());

    private void ApplyDriverButton(bool installed)
    {
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

    // ウィンドウのタイトルバーもダークにして、白文字のタイトルを読みやすくする
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // グローバルホットキー: ゲームにフォーカスしたまま再生/記録を操作する
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private const int WmHotkey = 0x0312;
    private const int HotkeyPlayId = 9009;
    private const int HotkeyRecordId = 9010;
    private IntPtr _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            const int DwmwaUseImmersiveDarkMode = 20;
            DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex)
        {
            CrashLog.LogException("ダークタイトルバー設定に失敗 (無視)", ex);
        }
        try
        {
            var source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProcHook);
            var f9 = RegisterHotKey(_hwnd, HotkeyPlayId, 0, 0x78);   // F9
            var f10 = RegisterHotKey(_hwnd, HotkeyRecordId, 0, 0x79); // F10
            CrashLog.Log($"ホットキー登録: F9={f9}, F10={f10}");
        }
        catch (Exception ex)
        {
            CrashLog.LogException("ホットキー登録に失敗 (無視)", ex);
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        handled = true;
        var id = wParam.ToInt32();
        if (id == HotkeyPlayId)
        {
            if (_playback.IsPlaying || _startCts is not null) StopPlay_Click(this, new RoutedEventArgs());
            else Play_Click(this, new RoutedEventArgs());
        }
        else if (id == HotkeyRecordId)
        {
            if (_recorder.IsRecording) StopRecord_Click(this, new RoutedEventArgs());
            else Record_Click(this, new RoutedEventArgs());
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyPlayId);
            UnregisterHotKey(_hwnd, HotkeyRecordId);
        }
        _startCts?.Cancel();
        _monitorTimer.Stop();
        if (_recorder.IsRecording) _recorder.StopRecording();
        _playback.Stop();
        _virtualPad?.Dispose();
        base.OnClosed(e);
    }
}
