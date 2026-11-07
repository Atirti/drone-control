namespace DroneControl;

/// <summary>Отклонение стика в единичном круге. X направлен вправо, Y — вверх.</summary>
public readonly record struct StickPosition(double X, double Y)
{
    public bool IsCentered => X == 0 && Y == 0;

    public static StickPosition Normalize(double x, double y, double deadZone = 0)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return default;
        }

        // При выходе за круг сохраняем направление, включая диагональное движение.
        double largestComponent = Math.Max(Math.Abs(x), Math.Abs(y));
        if (largestComponent > 1)
        {
            x /= largestComponent;
            y /= largestComponent;
        }
        double length = Math.Sqrt(x * x + y * y);
        deadZone = Math.Clamp(deadZone, 0, 0.99);
        if (length <= deadZone)
        {
            return default;
        }

        double magnitude = (Math.Min(length, 1) - deadZone) / (1 - deadZone);
        return new StickPosition(x / length * magnitude, y / length * magnitude);
    }
}

/// <summary>Mode 2: левый стик — газ/поворот, правый — тангаж/крен. Сети здесь нет.</summary>
public sealed class FlightInput
{
    public StickPosition Left
    {
        get; private set;
    }
    public StickPosition Right
    {
        get; private set;
    }

    public void SetLeft(StickPosition position) => Left = StickPosition.Normalize(position.X, position.Y);

    public void SetRight(StickPosition position) => Right = StickPosition.Normalize(position.X, position.Y);

    public void Release()
    {
        Left = default;
        Right = default;
    }

    /// <summary>Скорость ограничивает максимальное отклонение; положение стика задаёт его долю.</summary>
    public byte[] Packet(double speed, FlightFlags flags = FlightFlags.None)
    {
        double maximum = 127 * Math.Clamp(double.IsFinite(speed) ? speed : 10, 10, 100) / 100;
        int Axis(double value) => 128 + (int)Math.Round(maximum * value);

        return Protocol.Flight(
            roll: Axis(Right.X),
            pitch: Axis(Right.Y),
            throttle: Axis(Left.Y),
            yaw: Axis(Left.X),
            flags: flags);
    }
}
