using System.Runtime.CompilerServices;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// 再生の「読み戻し検証」。
///
/// 同時入力が落ちる問題の切り分け用: 再生中に仮想コントローラーを XInput 経由で
/// 読み戻し、送ったはずの状態と 1 フレームずつ照合する。
/// - 読み戻しが一致 → アプリとドライバは正しく出している (問題はゲーム側の
///   実機/仮想の競合などの可能性が高い)
/// - 読み戻しが一致しない → その時刻と内容をログに残す (再現証跡になる)
///
/// 照合はボタン完全一致、スティック/トリガーは小さな誤差を許容する。
/// 4ms 未満しか続かないフレームは読み戻し側の粒度では追えないため対象外とする。
/// </summary>
public sealed class PlaybackVerifier
{
    private readonly List<(long Qpc, GamepadState State)>[] _slots;
    private readonly object _gate = new();
    private readonly bool[] _seen = new bool[4];
    private readonly int[] _seenOrder = new int[4];
    private int _seenCount;
    private Thread? _thread;
    private volatile bool _running;
    private Recording? _expected;
    private long _startQpc;

    public PlaybackVerifier()
    {
        _slots = new List<(long Qpc, GamepadState State)>[4];
        for (var i = 0; i < 4; i++) _slots[i] = new(8192);
    }

    public sealed record Result(string Summary, string Detail);

    public void Begin(long startQpc, Recording expected)
    {
        _startQpc = startQpc;
        _expected = expected;
        lock (_gate)
        {
            _seenCount = 0;
            Array.Clear(_seen);
            for (var i = 0; i < 4; i++) _slots[i].Clear();
        }
        _running = true;
        _thread = new Thread(PollLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = "PadTas.Verify",
        };
        _thread.Start();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void PollLoop()
    {
        PreciseWait.AcquireTimerResolution();
        try
        {
            var freq = QpcClock.Frequency;
            var interval = freq / 1000;
            var next = QpcClock.Now;
            Func<bool> stop = () => !_running;
            Span<(int Slot, GamepadState State)> found = stackalloc (int, GamepadState)[4];
            while (_running)
            {
                var now = QpcClock.Now;
                // XInput はロックの外。追加だけを一度にまとめる (スロットごとのロックをやめる)。
                var foundCount = 0;
                for (var slot = 0; slot < 4; slot++)
                {
                    if (!XInput.TryGetState(slot, out var state, out _)) continue;
                    found[foundCount++] = (slot, state);
                }
                if (foundCount > 0)
                {
                    lock (_gate)
                    {
                        for (var i = 0; i < foundCount; i++)
                        {
                            var slot = found[i].Slot;
                            if (!_seen[slot])
                            {
                                _seen[slot] = true;
                                _seenOrder[_seenCount++] = slot;
                            }
                            _slots[slot].Add((now, found[i].State));
                        }
                    }
                }
                next += interval;
                PreciseWait.WaitUntil(next, stop);
                if (QpcClock.Now - next > interval * 4) next = QpcClock.Now;
            }
        }
        finally
        {
            PreciseWait.ReleaseThreadTimer();
            PreciseWait.ReleaseTimerResolution();
        }
    }

    public Result StopAndEvaluate()
    {
        _running = false;
        var thread = _thread;
        var stopped = thread is null || thread.Join(500);
        _thread = null;

        if (_expected is null)
            return new Result("検証データなし", "Begin が呼ばれていません。");

        // 仮想コントローラー候補: 非ニュートラルなサンプルが最も多いスロット。
        // 同数のときは、最初に観測したスロットを優先する (従来の辞書挿入順と同じ)。
        int slot;
        List<(long Qpc, GamepadState State)> samples;
        lock (_gate)
        {
            slot = -1;
            var bestNonNeutral = 0;
            for (var i = 0; i < _seenCount; i++)
            {
                var s = _seenOrder[i];
                var list = _slots[s];
                var n = 0;
                for (var j = 0; j < list.Count; j++)
                {
                    if (list[j].State != GamepadState.Neutral) n++;
                }
                if (n > bestNonNeutral) { bestNonNeutral = n; slot = s; }
            }
            if (slot < 0 || bestNonNeutral == 0)
                return new Result(
                    "仮想コントローラーを読み戻しで検出できませんでした (ドライバ未導入の可能性)",
                    "全スロットで非ニュートラルな入力が観測されませんでした。");
            // スレッドが止まっていればコピーしない。まだ動いていればスナップショットを切る。
            samples = stopped ? _slots[slot] : _slots[slot].ToList();
        }

        var frames = _expected.Frames;
        var minWindow = QpcClock.Frequency * 4 / 1000; // 4ms
        int checkedCount = 0, matched = 0, skipped = 0, missing = 0;
        var details = new List<string>();
        var sampleIndex = 0;

        for (var i = 0; i < frames.Count; i++)
        {
            var due = _startQpc + frames[i].TimestampTicks;
            var windowEnd = i + 1 < frames.Count
                ? _startQpc + frames[i + 1].TimestampTicks
                : _startQpc + _expected.EffectiveDurationTicks;
            if (windowEnd - due < minWindow) { skipped++; continue; }

            // ウィンドウ内の最後のサンプルを実測値とする
            GamepadState? actual = null;
            while (sampleIndex < samples.Count && samples[sampleIndex].Qpc < windowEnd)
            {
                if (samples[sampleIndex].Qpc >= due) actual = samples[sampleIndex].State;
                sampleIndex++;
            }
            // 次フレームのウィンドウのためにインデックスを少し戻す (境界共有)
            if (sampleIndex > 0) sampleIndex--;

            checkedCount++;
            if (actual is null) { missing++; continue; }
            var expected = frames[i].State;
            if (Matches(expected, actual.Value)) { matched++; continue; }
            if (details.Count < 10)
            {
                var t = (double)frames[i].TimestampTicks / QpcClock.Frequency;
                details.Add(
                    $"{t:F3}s: 期待 Buttons={expected.Buttons:X4} LX={expected.LeftX} LY={expected.LeftY} LT={expected.LeftTrigger} RT={expected.RightTrigger} / " +
                    $"実測 Buttons={actual.Value.Buttons:X4} LX={actual.Value.LeftX} LY={actual.Value.LeftY} LT={actual.Value.LeftTrigger} RT={actual.Value.RightTrigger}");
            }
        }

        var mismatched = checkedCount - matched - missing;
        var summary = mismatched == 0 && missing == 0
            ? $"読み戻し検証 OK: {matched}/{checkedCount} フレーム一致 (スロット {slot}, 対象外 {skipped})"
            : $"読み戻し検証で不一致: 一致 {matched}/{checkedCount}, 不一致 {mismatched}, 未観測 {missing} (スロット {slot})";
        var detail = summary + (details.Count > 0 ? "\n" + string.Join("\n", details) : "");
        return new Result(summary, detail);
    }

    private static bool Matches(GamepadState expected, GamepadState actual)
    {
        if (expected.Buttons != actual.Buttons) return false;
        if (Math.Abs(expected.LeftX - actual.LeftX) > 512) return false;
        if (Math.Abs(expected.LeftY - actual.LeftY) > 512) return false;
        if (Math.Abs(expected.RightX - actual.RightX) > 512) return false;
        if (Math.Abs(expected.RightY - actual.RightY) > 512) return false;
        if (Math.Abs(expected.LeftTrigger - actual.LeftTrigger) > 16) return false;
        if (Math.Abs(expected.RightTrigger - actual.RightTrigger) > 16) return false;
        return true;
    }
}
