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
    private readonly Dictionary<int, List<(long Qpc, GamepadState State)>> _samples = new();
    private Thread? _thread;
    private volatile bool _running;
    private Recording? _expected;
    private long _startQpc;

    public sealed record Result(string Summary, string Detail);

    public void Begin(long startQpc, Recording expected)
    {
        _startQpc = startQpc;
        _expected = expected;
        _samples.Clear();
        _running = true;
        _thread = new Thread(PollLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = "PadTas.Verify",
        };
        _thread.Start();
    }

    private void PollLoop()
    {
        var interval = QpcClock.Frequency / 1000;
        var next = QpcClock.Now;
        while (_running)
        {
            var now = QpcClock.Now;
            for (var slot = 0; slot < 4; slot++)
            {
                if (!XInput.TryGetState(slot, out var state, out _)) continue;
                lock (_samples)
                {
                    if (!_samples.TryGetValue(slot, out var list))
                        _samples[slot] = list = new List<(long, GamepadState)>();
                    list.Add((now, state));
                }
            }
            next += interval;
            while (_running)
            {
                var remaining = next - QpcClock.Now;
                if (remaining <= 0) break;
                if (remaining > QpcClock.Frequency / 500) Thread.Sleep(1);
                else Thread.SpinWait(32);
            }
            if (QpcClock.Now - next > interval * 4) next = QpcClock.Now;
        }
    }

    public Result StopAndEvaluate()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;

        if (_expected is null)
            return new Result("検証データなし", "Begin が呼ばれていません。");

        // 仮想コントローラー候補: 非ニュートラルなサンプルが最も多いスロット
        int slot = -1, bestNonNeutral = 0;
        lock (_samples)
        {
            foreach (var (s, list) in _samples)
            {
                var n = list.Count(x => x.State != GamepadState.Neutral);
                if (n > bestNonNeutral) { bestNonNeutral = n; slot = s; }
            }
        }
        if (slot < 0 || bestNonNeutral == 0)
            return new Result(
                "仮想コントローラーを読み戻しで検出できませんでした (ドライバ未導入の可能性)",
                "全スロットで非ニュートラルな入力が観測されませんでした。");

        List<(long Qpc, GamepadState State)> samples;
        lock (_samples) samples = _samples[slot].ToList();

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
