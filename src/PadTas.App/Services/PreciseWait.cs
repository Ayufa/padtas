using System.Runtime.InteropServices;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>
/// QPC の絶対期限まで待つ。
/// 長い待ちは高分解能ウェイト可能タイマ (無い環境は timeBeginPeriod(1) 後の Sleep)、
/// 期限直前だけスピンして合わせる。Sleep の誤差を次の期限へ持ち越さない。
/// </summary>
internal static class PreciseWait
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAccess = 0x00100000 | 0x00000002; // SYNCHRONIZE | TIMER_MODIFY_STATE
    private const int MaxCoarseSliceMs = 4;

    private static int _periodRefs;

    [ThreadStatic] private static IntPtr t_timer;
    [ThreadStatic] private static byte t_timerState; // 0 = 未試行, 1 = 使用可, 2 = 使えない

    public static void AcquireTimerResolution()
    {
        if (Interlocked.Increment(ref _periodRefs) == 1)
        {
            try { timeBeginPeriod(1); }
            catch { /* winmm が無い環境では Sleep の既定粒度のまま */ }
        }
    }

    public static void ReleaseTimerResolution()
    {
        if (Interlocked.Decrement(ref _periodRefs) == 0)
        {
            try { timeEndPeriod(1); }
            catch { /* 対応する begin が失敗している */ }
        }
    }

    /// <summary>このスレッドが作ったウェイト可能タイマを閉じる。ポーリングスレッドの finally から呼ぶ。</summary>
    public static void ReleaseThreadTimer()
    {
        var timer = t_timer;
        t_timer = IntPtr.Zero;
        t_timerState = 0;
        if (timer != IntPtr.Zero)
            CloseHandle(timer);
    }

    public static void WaitUntil(long dueQpc, Func<bool> shouldStop)
    {
        var freq = QpcClock.Frequency;
        var highRes = GetTimer() != IntPtr.Zero;
        // 高分解能タイマは約 0.5ms。そこから先はスピンして QPC 期限に合わせる。
        // タイマが無いときは timeBeginPeriod(1) 後の Sleep(1) が見える粒度に合わせ、約 1ms を残す。
        var spinMargin = highRes ? Math.Max(1, freq / 2500) : Math.Max(1, freq / 1000);

        while (!shouldStop())
        {
            var remaining = dueQpc - QpcClock.Now;
            if (remaining <= 0) return;

            if (remaining > spinMargin * 2)
            {
                CoarseWait(remaining - spinMargin, freq);
                continue;
            }

            // 50µs より手前は長めの pause、直前は短く回して突き抜けないようにする。
            Thread.SpinWait(remaining > freq / 20000 ? 32 : 4);
        }
    }

    private static void CoarseWait(long ticks, long freq)
    {
        if (ticks <= 0) return;

        var maxSlice = freq * MaxCoarseSliceMs / 1000;
        if (maxSlice < 1) maxSlice = 1;
        var slice = ticks < maxSlice ? ticks : maxSlice;
        var started = QpcClock.Now;

        var timer = GetTimer();
        if (timer != IntPtr.Zero)
        {
            var hundredNs = (long)((double)slice * 10_000_000.0 / freq);
            if (hundredNs < 1) hundredNs = 1;
            var due = -hundredNs;
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                var timeoutMs = (uint)(slice * 1000 / freq) + 2;
                if (timeoutMs < 1) timeoutMs = 1;
                WaitForSingleObject(timer, timeoutMs);
                // 以前のシグナルが残って時計が進まずに戻ったときだけ譲る。正常な起床では通らない。
                if (QpcClock.Now - started < freq / 100_000)
                    Thread.Sleep(1);
                return;
            }
        }

        var ms = (int)(slice * 1000 / freq);
        if (ms < 1) ms = 1;
        if (ms > MaxCoarseSliceMs) ms = MaxCoarseSliceMs;
        Thread.Sleep(ms);
    }

    private static IntPtr GetTimer()
    {
        if (t_timerState == 1) return t_timer;
        if (t_timerState == 2) return IntPtr.Zero;
        try
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAccess);
            if (handle == IntPtr.Zero)
            {
                t_timerState = 2;
                return IntPtr.Zero;
            }
            t_timer = handle;
            t_timerState = 1;
            return handle;
        }
        catch (EntryPointNotFoundException)
        {
            t_timerState = 2;
            return IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            t_timerState = 2;
            return IntPtr.Zero;
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
