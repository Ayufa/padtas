using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// 再生サービス。各フレームを「再生開始時刻 + フレーム相対時刻」の絶対期限で送るため、
/// 途中で多少引っかかっても後のフレームが遅れて累積しない (ドリフトなし)。
/// 期限直前はスピン待機で詰める。
/// </summary>
public sealed class PlaybackService
{
    private CancellationTokenSource? _cts;
    public bool IsPlaying => _cts is { IsCancellationRequested: false };

    public event Action<int, int>? Progress; // 現在フレーム / 総フレーム
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

    private void Loop(Recording recording, IVirtualGamepad output, CancellationToken token)
    {
        try
        {
            var start = QpcClock.Now;
            for (var i = 0; i < recording.Frames.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var due = PlaybackPlanner.DueTimestamp(recording, start, i);
                WaitUntil(due, token);
                if (token.IsCancellationRequested) break;
                output.Send(recording.Frames[i].State);
                Progress?.Invoke(i + 1, recording.Frames.Count);
            }
            // 終端まで最終状態を保持してから離す
            var endDue = start + recording.EffectiveDurationTicks;
            if (!token.IsCancellationRequested) WaitUntil(endDue, token);
        }
        finally
        {
            output.ReleaseAll();
            Finished?.Invoke();
        }
    }

    private static void WaitUntil(long dueQpc, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var remaining = dueQpc - QpcClock.Now;
            if (remaining <= 0) return;
            if (remaining > QpcClock.Frequency / 500) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }
}
