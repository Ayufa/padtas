using System.Diagnostics;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// 超高精度記録サービス。
///
/// 精度のための要点:
/// - UI スレッドとは別の専用スレッドでポーリングし、優先度を Highest に上げる
/// - ポーリング間隔は Stopwatch (QPC) ベースで刻み、残り 1ms 未満はスピンで詰める
///   (Thread.Sleep の粒度 約15.6ms では 60fps のフレーム内精度すら出ないため)
/// - 記録は「変化した瞬間だけ」。デッドゾーン等は一切かけず生値を保存する
/// - ポーリング周期は 250 / 500 / 1000 (既定) / 2000 Hz から選択可能。
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

    private void PollLoop(int userIndex, int pollingRateHz)
    {
        var intervalTicks = QpcClock.Frequency / pollingRateHz;
        var next = QpcClock.Now;
        while (_running)
        {
            if (XInput.TryGetState(userIndex, out var state, out _))
            {
                Engine.Feed(QpcClock.Now, state);
                LiveState?.Invoke(state);
            }

            next += intervalTicks;
            // 期限まで: 長い分は Sleep(1) で譲り、最後の数百µs はスピンで正確に合わせる
            while (_running)
            {
                var remaining = next - QpcClock.Now;
                if (remaining <= 0) break;
                if (remaining > QpcClock.Frequency / 500) Thread.Sleep(1); // >2ms
                else Thread.SpinWait(64);
            }
            // 大幅に遅れた場合は次周期を現在に引き直す (遅延を伝播させない)
            if (QpcClock.Now - next > intervalTicks * 4) next = QpcClock.Now;
        }
    }
}
