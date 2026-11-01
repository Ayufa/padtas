using System.Runtime.CompilerServices;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// 超高精度記録サービス。
///
/// 精度のための要点:
/// - UI スレッドとは別の専用スレッドでポーリングし、優先度を Highest に上げる
/// - ポーリング間隔は Stopwatch (QPC) の絶対期限で刻む。長い待ちは高分解能タイマ、
///   期限直前だけスピン (Thread.Sleep の既定粒度 約15.6ms ではフレーム内精度が出ない)
/// - 記録は「変化した瞬間だけ」。デッドゾーン等は一切かけず生値を保存する
/// - ライブ表示のイベントは約 30fps に間引く (記録自体は全サンプルをエンジンへ渡す)
/// - ポーリング周期は 250 / 500 / 1000 (既定) / 2000 / 4000 / 8000 Hz から選択可能。
///   ただし実際に変化が観測できる速さはコントローラー自体のポーリングレート
///   (例: 純正 Xbox コントローラーは 125Hz 程度) が上限になる点に注意。
/// </summary>
public sealed class RecorderService
{
    private Thread? _thread;
    private volatile bool _running;

    public RecorderEngine Engine { get; } = new(analogEpsilon: 0);
    public bool IsRecording => _running;
    public event Action<GamepadState>? LiveState;

    public void StartRecording(int userIndex, int pollingRateHz)
    {
        if (_running) throw new InvalidOperationException("すでに記録中です。");
        Engine.Start(new Recording
        {
            ControllerName = $"XInput #{userIndex}",
            PollingRateHz = pollingRateHz,
            QpcFrequency = QpcClock.Frequency,
            CreatedAt = DateTimeOffset.Now,
        });
        _running = true;
        _thread = new Thread(() => PollLoop(userIndex, pollingRateHz))
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "PadTas.Recorder",
        };
        _thread.Start();
    }

    public Recording StopRecording()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        return Engine.Stop();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void PollLoop(int userIndex, int pollingRateHz)
    {
        PreciseWait.AcquireTimerResolution();
        try
        {
            var freq = QpcClock.Frequency;
            var intervalTicks = freq / pollingRateHz;
            var uiInterval = freq * 33 / 1000;
            if (uiInterval < 1) uiInterval = 1;
            var next = QpcClock.Now;
            var nextUi = next;
            Func<bool> stop = () => !_running;
            while (_running)
            {
                if (XInput.TryGetState(userIndex, out var state, out _))
                {
                    var now = QpcClock.Now;
                    // 1 LSB でも変化すればフレームになる。ここは間引かない。
                    Engine.Feed(now, state);
                    if (now >= nextUi)
                    {
                        nextUi = now + uiInterval;
                        LiveState?.Invoke(state);
                    }
                }

                next += intervalTicks;
                PreciseWait.WaitUntil(next, stop);
                // 大幅に遅れた場合は次周期を現在に引き直す (遅延を伝播させない)
                var after = QpcClock.Now;
                if (after - next > intervalTicks * 4) next = after;
            }
        }
        finally
        {
            PreciseWait.ReleaseThreadTimer();
            PreciseWait.ReleaseTimerResolution();
        }
    }
}
