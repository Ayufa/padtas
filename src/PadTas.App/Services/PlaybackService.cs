using System.Runtime.CompilerServices;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// 再生サービス。各フレームを「再生開始時刻 + フレーム相対時刻」の絶対期限で送るため、
/// 途中で多少引っかかっても後のフレームが遅れて累積しない (ドリフトなし)。
/// 期限の手前は高分解能タイマで待ち、直前だけスピンで詰める。
/// </summary>
public sealed class PlaybackService
{
    private CancellationTokenSource? _cts;
    public bool IsPlaying => _cts is { IsCancellationRequested: false };

    public event Action<int, int>? Progress; // 現在フレーム / 総フレーム
    public event Action<GamepadState>? StateSent; // 送信した状態 (コントローラー表示用)
    public event Action<long>? Started; // 再生開始 QPC (読み戻し検証の基準時刻)
    public event Action? Finished;

    public void Play(Recording recording, IVirtualGamepad output)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Loop(recording, output, token))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "PadTas.Playback",
        };
        thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Loop(Recording recording, IVirtualGamepad output, CancellationToken token)
    {
        PreciseWait.AcquireTimerResolution();
        try
        {
            try
            {
                var start = QpcClock.Now;
                Started?.Invoke(start);
                var frames = recording.Frames;
                var count = frames.Count;
                var freq = QpcClock.Frequency;
                var uiInterval = freq * 33 / 1000;
                if (uiInterval < 1) uiInterval = 1;
                var nextUi = start;
                var onState = StateSent;
                var onProgress = Progress;
                Func<bool> stop = () => token.IsCancellationRequested;
                for (var i = 0; i < count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    var frame = frames[i];
                    var due = PlaybackPlanner.DueTimestamp(recording, start, i);
                    PreciseWait.WaitUntil(due, stop);
                    if (token.IsCancellationRequested) break;
                    output.Send(frame.State);
                    if (onState is not null)
                    {
                        var now = QpcClock.Now;
                        if (now >= nextUi)
                        {
                            nextUi = now + uiInterval;
                            onState(frame.State);
                        }
                    }
                    onProgress?.Invoke(i + 1, count);
                }
                // 終端まで最終状態を保持してから離す
                var endDue = start + recording.EffectiveDurationTicks;
                if (!token.IsCancellationRequested) PreciseWait.WaitUntil(endDue, stop);
            }
            finally
            {
                output.ReleaseAll();
                Finished?.Invoke();
            }
        }
        finally
        {
            PreciseWait.ReleaseThreadTimer();
            PreciseWait.ReleaseTimerResolution();
        }
    }
}
