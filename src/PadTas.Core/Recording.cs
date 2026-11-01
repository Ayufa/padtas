using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PadTas.Core;

/// <summary>高精度時計。Windows では QueryPerformanceCounter (分解能 約100ns) に対応する。</summary>
public static class QpcClock
{
    public static long Frequency => Stopwatch.Frequency;
    public static long Now => Stopwatch.GetTimestamp();
    public static double TicksToSeconds(long ticks) => (double)ticks / Frequency;
    public static long SecondsToTicks(double seconds) => (long)(seconds * Frequency);
}

/// <summary>記録開始時点を 0 とした相対 QPC tick と、その瞬間の状態。</summary>
public readonly record struct InputFrame(long TimestampTicks, GamepadState State);

/// <summary>
/// 1回の記録 (セグメント) の中身。
/// フレームは「状態が変化した瞬間」だけを保存する (変化なしのポーリング結果は捨てる)。
/// そのため 1000Hz でポーリングしても、ファイルは実際の入力変化の分だけになる。
/// </summary>
public sealed class Recording
{
    public string ControllerName { get; set; } = "";
    public int PollingRateHz { get; set; } = 1000;
    public long QpcFrequency { get; set; } = QpcClock.Frequency;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<InputFrame> Frames { get; } = new();

    /// <summary>最後のフレーム + 終端として明示した長さ。終端未設定なら最後のフレーム時刻。</summary>
    public long DurationTicks { get; set; }

    public long EffectiveDurationTicks =>
        DurationTicks > 0 ? DurationTicks
        : Frames.Count > 0 ? Frames[^1].TimestampTicks
        : 0;

    public double DurationSeconds => (double)EffectiveDurationTicks / QpcFrequency;

    /// <summary>任意の時刻における状態 (その時刻以前の最後のフレーム)。シーク・続き記録の初期状態に使う。</summary>
    public GamepadState StateAt(long ticks)
    {
        var state = GamepadState.Neutral;
        foreach (var f in Frames)
        {
            if (f.TimestampTicks > ticks) break;
            state = f.State;
        }
        return state;
    }
}

/// <summary>
/// ポーリングループからサンプルを受け取り、変化した瞬間だけを Recording に積むエンジン。
/// 入力デバイス (XInput / DirectInput / HID) に依存しないので、単体テストができる。
/// 専用スレッドから Feed を呼ぶ想定 (UI スレッドでは呼ばない)。
/// </summary>
public sealed class RecorderEngine
{
    private readonly int _analogEpsilon;
    private long _startTimestamp;
    private GamepadState _lastRecorded;
    private bool _hasLast;

    public RecorderEngine(int analogEpsilon = 0) => _analogEpsilon = analogEpsilon;

    public Recording Current { get; private set; } = new();
    public bool IsRecording { get; private set; }

    public void Start(Recording header)
    {
        Current = header;
        // 変化が密でも、記録開始直後の配列拡張でポーリングが止まらないようにする。
        var hint = header.PollingRateHz > 0 ? header.PollingRateHz * 8 : 8192;
        if (hint > 262_144) hint = 262_144;
        if (Current.Frames.Capacity < hint)
            Current.Frames.EnsureCapacity(hint);
        _startTimestamp = QpcClock.Now;
        _hasLast = false;
        _lastRecorded = default;
        IsRecording = true;
        // 開始直後の状態は必ず 1 フレーム目として記録する (続き記録の接続点になる)
        Feed(QpcClock.Now, GamepadState.Neutral, force: true);
    }

    /// <summary>ポーリングで得たサンプルを 1 つ渡す。変化がなければ何もしない。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Feed(long qpcTimestamp, GamepadState state, bool force = false)
    {
        if (!IsRecording) return;
        var relative = qpcTimestamp - _startTimestamp;
        if (relative < 0) relative = 0;

        if (!force && _hasLast && !state.DiffersFrom(_lastRecorded, _analogEpsilon))
            return;

        Current.Frames.Add(new InputFrame(relative, state));
        _lastRecorded = state;
        _hasLast = true;
    }

    public Recording Stop()
    {
        if (IsRecording)
            Current.DurationTicks = QpcClock.Now - _startTimestamp;
        IsRecording = false;
        return Current;
    }
}

/// <summary>
/// 再生スケジューラ。相対時刻ではなく「再生開始 QPC + フレーム時刻」という
/// 絶対時刻で各フレームの期限を決めるため、スリープ誤差が累積してドリフトしない。
/// </summary>
public static class PlaybackPlanner
{
    public static long DueTimestamp(Recording recording, long playbackStartQpc, int frameIndex)
        => playbackStartQpc + recording.Frames[frameIndex].TimestampTicks;

    /// <summary>期限まで待つための残り時間。2ms 未満は呼び出し側でスピン待機する想定。</summary>
    public static TimeSpan Remaining(long dueQpc, long nowQpc)
        => TimeSpan.FromSeconds((double)Math.Max(0, dueQpc - nowQpc) / QpcClock.Frequency);
}
