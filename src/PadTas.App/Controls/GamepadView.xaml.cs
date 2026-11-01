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
    private static readonly SolidColorBrush Idle = Frozen(0x26, 0x28, 0x2F);
    private static readonly SolidColorBrush Accent = Frozen(0x5E, 0x9B, 0xFF);
    private static readonly SolidColorBrush AColor = Frozen(0x3F, 0xB9, 0x6F);
    private static readonly SolidColorBrush BColor = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly SolidColorBrush XColor = Frozen(0x5E, 0x9B, 0xFF);
    private static readonly SolidColorBrush YColor = Frozen(0xE8, 0xC3, 0x3C);
    private static readonly SolidColorBrush KnobIdle = Frozen(0x33, 0x36, 0x3F);
    private static readonly SolidColorBrush KnobPressed = Frozen(0x5E, 0x9B, 0xFF);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // スティックノブの中心位置 (XAML の初期配置)
    private const double LsBaseLeft = 121, LsBaseTop = 123;
    private const double RsBaseLeft = 351, RsBaseTop = 199;
    private const double StickTravel = 13; // ノブが動く最大ピクセル

    public GamepadView() => InitializeComponent();

    public void SetState(GamepadState s)
    {
        var b = (GamepadButtons)s.Buttons;

        SetPad(DUp, Down(b, GamepadButtons.DPadUp));
        SetPad(DDown, Down(b, GamepadButtons.DPadDown));
        SetPad(DLeft, Down(b, GamepadButtons.DPadLeft));
        SetPad(DRight, Down(b, GamepadButtons.DPadRight));
        SetPad(LbPad, Down(b, GamepadButtons.LeftShoulder));
        SetPad(RbPad, Down(b, GamepadButtons.RightShoulder));

        SetFill(BtnA, Down(b, GamepadButtons.A) ? AColor : Idle);
        SetFill(BtnB, Down(b, GamepadButtons.B) ? BColor : Idle);
        SetFill(BtnX, Down(b, GamepadButtons.X) ? XColor : Idle);
        SetFill(BtnY, Down(b, GamepadButtons.Y) ? YColor : Idle);
        SetFill(BtnBack, Down(b, GamepadButtons.Back) ? Accent : Idle);
        SetFill(BtnStart, Down(b, GamepadButtons.Start) ? Accent : Idle);

        // スティック: 倒し量に応じてノブを移動 (Y は上がマイナス値なので符号反転)
        Canvas.SetLeft(LsKnob, LsBaseLeft + s.LeftX / 32767.0 * StickTravel);
        Canvas.SetTop(LsKnob, LsBaseTop - s.LeftY / 32767.0 * StickTravel);
        Canvas.SetLeft(RsKnob, RsBaseLeft + s.RightX / 32767.0 * StickTravel);
        Canvas.SetTop(RsKnob, RsBaseTop - s.RightY / 32767.0 * StickTravel);
        SetFill(LsKnob, Down(b, GamepadButtons.LeftThumb) ? KnobPressed : KnobIdle);
        SetFill(RsKnob, Down(b, GamepadButtons.RightThumb) ? KnobPressed : KnobIdle);

        // トリガー: 0..255 をバーの幅 0..96px に
        LtFill.Width = s.LeftTrigger / 255.0 * 96;
        RtFill.Width = s.RightTrigger / 255.0 * 96;
    }

    private static bool Down(GamepadButtons value, GamepadButtons flag) => (value & flag) != 0;

    private static void SetPad(Border pad, bool on)
    {
        var brush = on ? Accent : Idle;
        if (!ReferenceEquals(pad.Background, brush)) pad.Background = brush;
    }

    private static void SetFill(Shape shape, Brush brush)
    {
        if (!ReferenceEquals(shape.Fill, brush)) shape.Fill = brush;
    }

    public void Reset() => SetState(GamepadState.Neutral);
}
