using System.IO;
using System.Windows;
using Microsoft.Win32;
using PadTas.App.Services;
using PadTas.Core;

namespace PadTas.App;

public partial class MainWindow : Window
{
    private readonly RecorderService _recorder = new();
    private readonly PlaybackService _playback = new();
    private IVirtualGamepad? _virtualPad;
    private TasProject? _project;

    public MainWindow()
    {
        InitializeComponent();
        _recorder.LiveState += state => Dispatcher.BeginInvoke(() =>
            LiveStateText.Text =
                $"Buttons: {state.Buttons:X4}  LX: {state.LeftX}  LY: {state.LeftY}  LT: {state.LeftTrigger}");
        StatusText.Text = $"バージョン: {UpdateService.CurrentVersion} / プロジェクトフォルダを選んでください。";
        Loaded += async (_, _) =>
        {
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
            SegmentList.Items.Add($"{i++}. {s.Name}  ({s.DurationSeconds:F3} 秒)  [{s.FileName}]");
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

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _project.Segments.Count == 0)
        { SetStatus("再生できるセグメントがありません。"); return; }
        try
        {
            _virtualPad ??= new ViGEmGamepad();
        }
        catch (Exception ex)
        {
            SetStatus("仮想コントローラーを作れませんでした。ViGEmBus ドライバ (1.22.0) が必要です: " + ex.Message);
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
        if (_recorder.IsRecording) _recorder.StopRecording();
        _playback.Stop();
        _virtualPad?.Dispose();
        base.OnClosed(e);
    }
}
