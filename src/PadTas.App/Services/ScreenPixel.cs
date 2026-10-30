using System.Runtime.InteropServices;

namespace PadTas.App.Services;

/// <summary>
/// 画面上の 1 ピクセルの色取得 (画面トリガーの開始同期用)。
/// ロード中は真っ黒な場所などを拾っておき、色が変わった瞬間に再生を始める。
/// </summary>
public static class ScreenPixel
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    public struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>COLORREF (0x00BBGGRR) を RGB に分解して返す。取得失敗は null。</summary>
    public static (byte R, byte G, byte B)? Sample(int x, int y)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            var color = GetPixel(hdc, x, y);
            if (color == 0xFFFFFFFF) return null;
            return ((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static int Difference((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
