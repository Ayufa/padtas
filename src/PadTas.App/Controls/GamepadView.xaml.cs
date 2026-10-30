using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PadTas.Core;

namespace PadTas.App.Controls;

/// <summary>
/// コントローラーの形をした入力モニター。
/// SetState に現在の状態を渡すと、押されているボタンが光り、
/// スティックが倒した方向へ動き、トリガーのバーが伸びる。
/// 記録中・再生中・待機中 (実機モニター) のいずれからも同じ方法で駆動する。
/// </summary>
public partial class GamepadView : UserControl
{
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x26, 0x28, 0x2F));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5E, 0x9B, 0xFF));
    private static readonly Brush AColor = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x6F));
    private static readonly Brush BColor = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush XColor = new SolidColorBrush(Color.FromRgb(0x5E, 0x9B, 0xFF));
    private static readonly Brush YColor = new SolidColorBrush(Color.FromRgb(0xE8, 0xC3, 0x3C));
    private static readonly Brush KnobIdle = new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3F));
    private static readonly Brush KnobPressed = new SolidColorBrush(Color.FromRgb(0x5E, 0x9B, 0xFF));

    // スティックノブの中心位置 (XAML の初期配置)
    private const double LsBaseLeft = 121, LsBaseTop = 123;
    private const double RsBaseLeft = 351, RsBaseTop = 199;
    private const double StickTravel = 13; // ノブが動く最大ピクセル

    public GamepadView() => InitializeComponent();

    public void SetState(GamepadState s)
    {
        var b = (GamepadButtons)s.Buttons;

        SetPad(DUp, b.HasFlag(GamepadButtons.DPadUp));
        SetPad(DDown, b.HasFlag(GamepadButtons.DPadDown));
        SetPad(DLeft, b.HasFlag(GamepadButtons.DPadLeft));
        SetPad(DRight, b.HasFlag(GamepadButtons.DPadRight));
        SetPad(LbPad, b.HasFlag(GamepadButtons.LeftShoulder));
        SetPad(RbPad, b.HasFlag(GamepadButtons.RightShoulder));

        BtnA.Fill = b.HasFlag(GamepadButtons.A) ? AColor : Idle;
        BtnB.Fill = b.HasFlag(GamepadButtons.B) ? BColor : Idle;
        BtnX.Fill = b.HasFlag(GamepadButtons.X) ? XColor : Idle;
        BtnY.Fill = b.HasFlag(GamepadButtons.Y) ? YColor : Idle;
        BtnBack.Fill = b.HasFlag(GamepadButtons.Back) ? Accent : Idle;
        BtnStart.Fill = b.HasFlag(GamepadButtons.Start) ? Accent : Idle;

        // スティック: 倒し量に応じてノブを移動 (Y は上がマイナス値なので符号反転)
        Canvas.SetLeft(LsKnob, LsBaseLeft + s.LeftX / 32767.0 * StickTravel);
        Canvas.SetTop(LsKnob, LsBaseTop - s.LeftY / 32767.0 * StickTravel);
        Canvas.SetLeft(RsKnob, RsBaseLeft + s.RightX / 32767.0 * StickTravel);
        Canvas.SetTop(RsKnob, RsBaseTop - s.RightY / 32767.0 * StickTravel);
        LsKnob.Fill = b.HasFlag(GamepadButtons.LeftThumb) ? KnobPressed : KnobIdle;
        RsKnob.Fill = b.HasFlag(GamepadButtons.RightThumb) ? KnobPressed : KnobIdle;

        // トリガー: 0..255 をバーの幅 0..96px に
        LtFill.Width = s.LeftTrigger / 255.0 * 96;
        RtFill.Width = s.RightTrigger / 255.0 * 96;
    }

    private static void SetPad(Border pad, bool on) => pad.Background = on ? Accent : Idle;

    public void Reset() => SetState(GamepadState.Neutral);
}
