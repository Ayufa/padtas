using System.Runtime.CompilerServices;

namespace PadTas.Core;

/// <summary>
/// 1サンプル分のコントローラー状態。すべて「生値」のまま保持する。
///
/// 超繊細な操作を再現するため、記録時にはデッドゾーン・平滑化・丸めを一切かけない。
/// - スティック: XInput の生値 (short, -32768..32767) をそのまま保存 (ロスレス)
/// - トリガー  : XInput の生値 (byte, 0..255) をそのまま保存 (ロスレス)
/// - ボタン    : 16bit ビットマスク (XInput の wButtons と同じ並び)
/// </summary>
public readonly record struct GamepadState(
    ushort Buttons,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger)
{
    public static GamepadState Neutral => new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 2つの状態が「記録する価値のある変化」を含むか。
    /// epsilon = 0 (既定) のとき 1 LSB の変化でも変化として扱う = 完全ロスレス。
    /// ノイズの多いスティックでファイル量を抑えたい場合だけ epsilon を上げる。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool DiffersFrom(GamepadState other, int analogEpsilon = 0)
    {
        if (analogEpsilon == 0)
        {
            // 1 LSB も捨てない。差が 0 かどうかだけを見る (Abs より短い)。
            return Buttons != other.Buttons
                | (LeftX != other.LeftX)
                | (LeftY != other.LeftY)
                | (RightX != other.RightX)
                | (RightY != other.RightY)
                | (LeftTrigger != other.LeftTrigger)
                | (RightTrigger != other.RightTrigger);
        }

        if (Buttons != other.Buttons) return true;
        if (Math.Abs(LeftX - other.LeftX) > analogEpsilon) return true;
        if (Math.Abs(LeftY - other.LeftY) > analogEpsilon) return true;
        if (Math.Abs(RightX - other.RightX) > analogEpsilon) return true;
        if (Math.Abs(RightY - other.RightY) > analogEpsilon) return true;
        if (Math.Abs(LeftTrigger - other.LeftTrigger) > analogEpsilon) return true;
        if (Math.Abs(RightTrigger - other.RightTrigger) > analogEpsilon) return true;
        return false;
    }
}

/// <summary>XInput のボタンビット (wButtons) と同じ定義。</summary>
[Flags]
public enum GamepadButtons : ushort
{
    None = 0x0000,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}
