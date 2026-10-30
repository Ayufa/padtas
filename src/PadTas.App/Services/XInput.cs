using System.Runtime.InteropServices;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>XInput (xinput1_4.dll) の最小 P/Invoke ラッパ。</summary>
public static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    private const uint ErrorSuccess = 0;

    /// <summary>指定スロットの現在状態を読む。未接続なら false。値は生値のまま返す。</summary>
    public static bool TryGetState(int userIndex, out GamepadState state, out uint packetNumber)
    {
        state = GamepadState.Neutral;
        packetNumber = 0;
        if (XInputGetState((uint)userIndex, out var native) != ErrorSuccess) return false;
        packetNumber = native.PacketNumber;
        state = new GamepadState(
            native.Gamepad.Buttons,
            native.Gamepad.LeftX, native.Gamepad.LeftY,
            native.Gamepad.RightX, native.Gamepad.RightY,
            native.Gamepad.LeftTrigger, native.Gamepad.RightTrigger);
        return true;
    }
}
