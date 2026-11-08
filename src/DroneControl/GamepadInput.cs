namespace DroneControl;

public readonly record struct GamepadState(uint DeviceId, string Name, StickPosition Left, StickPosition Right);

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
