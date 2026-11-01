using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PadTas.Core;

namespace PadTas.App.Services;

/// <summary>再生出力先の抽象。将来ドライバを差し替えられるようにインターフェース越しに使う。</summary>
public interface IVirtualGamepad : IDisposable
{
    void Send(GamepadState state);
    void ReleaseAll();
}

/// <summary>
/// ViGEmBus 経由の仮想 Xbox 360 コントローラー。
/// 前提: ViGEmBus ドライバ 1.22.0 がインストールされていること
/// (未インストール時は例外 → UI 側で導入案内を出す)。
/// 注意: ViGEmBus は 2023 年に開発終了 (アーカイブ済み) だが、最終版は署名済みで
/// Windows 10 / 11 で動作する。当面の標準出力とし、IVirtualGamepad で差し替え可能にしておく。
/// </summary>
public sealed class ViGEmGamepad : IVirtualGamepad
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;

    public ViGEmGamepad()
    {
        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.Connect();
    }

    public void Send(GamepadState s)
    {
        var b = (GamepadButtons)s.Buttons;
        _pad.SetButtonState(Xbox360Button.A, Down(b, GamepadButtons.A));
        _pad.SetButtonState(Xbox360Button.B, Down(b, GamepadButtons.B));
        _pad.SetButtonState(Xbox360Button.X, Down(b, GamepadButtons.X));
        _pad.SetButtonState(Xbox360Button.Y, Down(b, GamepadButtons.Y));
        _pad.SetButtonState(Xbox360Button.Start, Down(b, GamepadButtons.Start));
        _pad.SetButtonState(Xbox360Button.Back, Down(b, GamepadButtons.Back));
        _pad.SetButtonState(Xbox360Button.LeftShoulder, Down(b, GamepadButtons.LeftShoulder));
        _pad.SetButtonState(Xbox360Button.RightShoulder, Down(b, GamepadButtons.RightShoulder));
        _pad.SetButtonState(Xbox360Button.LeftThumb, Down(b, GamepadButtons.LeftThumb));
        _pad.SetButtonState(Xbox360Button.RightThumb, Down(b, GamepadButtons.RightThumb));
        _pad.SetButtonState(Xbox360Button.Up, Down(b, GamepadButtons.DPadUp));
        _pad.SetButtonState(Xbox360Button.Down, Down(b, GamepadButtons.DPadDown));
        _pad.SetButtonState(Xbox360Button.Left, Down(b, GamepadButtons.DPadLeft));
        _pad.SetButtonState(Xbox360Button.Right, Down(b, GamepadButtons.DPadRight));
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftX);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftY);
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RightX);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RightY);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
        _pad.SubmitReport();
    }

    public void ReleaseAll() => Send(GamepadState.Neutral);

    private static bool Down(GamepadButtons value, GamepadButtons flag) => (value & flag) != 0;

    public void Dispose()
    {
        try { ReleaseAll(); _pad.Disconnect(); } catch { /* 終了時は握りつぶす */ }
        _client.Dispose();
    }
}
