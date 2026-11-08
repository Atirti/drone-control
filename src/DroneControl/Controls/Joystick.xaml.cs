using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DroneControl.Controls;

/// <summary>Пружинный стик с захватом мыши или одного пальца; два экземпляра поддерживают multitouch.</summary>
public partial class Joystick : UserControl
{
    private const double Travel = 60;
    private TouchDevice? activeTouch;
    private bool draggingMouse;

    public event Action<StickPosition>? ValueChanged;

    public Joystick()
    {
        InitializeComponent();
        IsEnabledChanged += (_, _) => ResetInput();
        Unloaded += (_, _) => ResetInput();
    }

    /// <summary>Меняет положение ручки без события ввода.</summary>
    public void Display(StickPosition position)
    {
        KnobOffset.X = position.X * Travel;
        KnobOffset.Y = -position.Y * Travel;
    }

    public void ResetInput()
    {
        var touch = activeTouch;
        activeTouch = null;
        draggingMouse = false;
        touch?.Capture(null);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Display(default);
        ValueChanged?.Invoke(default);
    }

    private void MoveTo(Point point)
    {
        var position = StickPosition.Normalize((point.X - 90) / Travel, (90 - point.Y) / Travel, 0.05);
        Display(position);
        ValueChanged?.Invoke(position);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left || e.StylusDevice != null || activeTouch != null)
        {
            return;
        }

        draggingMouse = CaptureMouse();
        if (draggingMouse)
        {
            MoveTo(e.GetPosition(Surface));
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingMouse)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                MoveTo(e.GetPosition(Surface));
            }
            else
            {
                ResetInput();
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (draggingMouse && e.ChangedButton == MouseButton.Left)
        {
            ResetInput();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (draggingMouse)
        {
            ResetInput();
        }
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (activeTouch == null && !draggingMouse && e.TouchDevice.Capture(this))
        {
            activeTouch = e.TouchDevice;
            MoveTo(e.GetTouchPoint(Surface).Position);
        }
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (e.TouchDevice == activeTouch)
        {
            MoveTo(e.GetTouchPoint(Surface).Position);
        }
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (e.TouchDevice == activeTouch)
        {
            ResetInput();
        }
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        if (e.TouchDevice == activeTouch)
        {
            ResetInput();
        }
    }
}
