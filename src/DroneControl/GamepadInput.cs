namespace DroneControl;

[Flags]
public enum GamepadButtons
{
    None = 0,
    LeftShoulder = 1,
    DpadUp = 2,
    DpadDown = 4,
    B = 8
}

[Flags]
public enum GamepadActions
{
    None = 0,
    NextSpeed = 1,
    Takeoff = 2,
    Land = 4,
    Stop = 8
}

public readonly record struct GamepadState(
    uint DeviceId, string Name, StickPosition Left, StickPosition Right,
    GamepadButtons Buttons = GamepadButtons.None);

/// <summary>Разовые нажатия. Центрирование стиков не сбрасывает историю кнопок.</summary>
public sealed class GamepadButtonInput
{
    private uint? deviceId;
    private GamepadButtons previousButtons;

    public GamepadActions Update(GamepadState? snapshot, bool isActive, bool canFly)
    {
        if (!isActive || snapshot is not { } state)
        {
            Reset();
            return GamepadActions.None;
        }

        // Уже зажатые при подключении или возврате фокуса кнопки нужно отпустить.
        var pressed = deviceId == state.DeviceId
            ? state.Buttons & ~previousButtons
            : GamepadButtons.None;
        deviceId = state.DeviceId;
        previousButtons = state.Buttons;

        if (state.Buttons.HasFlag(GamepadButtons.B))
        {
            return pressed.HasFlag(GamepadButtons.B) ? GamepadActions.Stop : GamepadActions.None;
        }

        var actions = pressed.HasFlag(GamepadButtons.LeftShoulder)
            ? GamepadActions.NextSpeed : GamepadActions.None;
        if (canFly)
        {
            // При конфликте направлений приоритет у посадки.
            if (pressed.HasFlag(GamepadButtons.DpadDown))
            {
                actions |= GamepadActions.Land;
            }
            else if (pressed.HasFlag(GamepadButtons.DpadUp) &&
                     !state.Buttons.HasFlag(GamepadButtons.DpadDown))
            {
                actions |= GamepadActions.Takeoff;
            }
        }

        return actions;
    }

    public void Reset()
    {
        deviceId = null;
        previousButtons = GamepadButtons.None;
    }
}

/// <summary>Преобразование осей SDL и допуск ввода после непрерывного центрирования.</summary>
public sealed class GamepadInput
{
    private static readonly TimeSpan CenteringTime = TimeSpan.FromMilliseconds(300);
    private uint? deviceId;
    private TimeSpan? centeredSince;

    public bool IsReady { get; private set; }

    /// <summary>В SDL отрицательный Y направлен вверх; в модели полёта вверх положителен.</summary>
    public static StickPosition Normalize(short x, short y)
    {
        static double Axis(short value) => value / (value < 0 ? 32768.0 : 32767.0);
        return StickPosition.Normalize(Axis(x), -Axis(y), deadZone: 0.18);
    }

    /// <param name="now">Монотонное время; не DateTime и не часы системы.</param>
    public bool Update(GamepadState? snapshot, TimeSpan now)
    {
        if (snapshot is not { } state)
        {
            Reset();
            return false;
        }

        if (deviceId != state.DeviceId)
        {
            Reset();
            deviceId = state.DeviceId;
        }

        if (!IsReady)
        {
            if (!state.Left.IsCentered || !state.Right.IsCentered)
            {
                centeredSince = null;
                return false;
            }

            centeredSince ??= now;
            IsReady = now - centeredSince.Value >= CenteringTime;
        }

        return IsReady;
    }

    public void Reset()
    {
        deviceId = null;
        centeredSince = null;
        IsReady = false;
    }
}
