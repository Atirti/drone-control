using System.Buffers.Binary;

namespace DroneControl;

public enum DroneProfile
{
    Tx,
    Bl
}

[Flags]
public enum FlightFlags : byte
{
    None = 0,
    Takeoff = 1,
    Land = 2,
    Stop = 4,
    Flip = 8,
    Headless = 16,
    Unlock = 32,
    Light = 64,
    Calibrate = 128
}

/// <summary>Формирование полётных команд и транспортных оболочек без сетевого ввода-вывода.</summary>
public static class Protocol
{
    /// <summary>Восстанавливает 8-байтовую команду APK. Нейтраль газа 128 не означает остановку.</summary>
    public static byte[] Flight(
        int roll = 128,
        int pitch = 128,
        int throttle = 128,
        int yaw = 128,
        FlightFlags flags = FlightFlags.None)
    {
        byte[] packet =
        [
            0x66,
            (byte)Math.Clamp(roll, 1, 255),
            (byte)Math.Clamp(pitch, 1, 255),
            // Значение газа 1 оригинальное приложение передаёт как 0.
            (byte)(throttle == 1 ? 0 : Math.Clamp(throttle, 0, 255)),
            (byte)Math.Clamp(yaw, 1, 255),
            (byte)flags,
            0, // XOR заполняется ниже.
            0x99
        ];
        packet[6] = (byte)(packet[1] ^ packet[2] ^ packet[3] ^ packet[4] ^ packet[5]);
        return packet;
    }

    /// <summary>Добавляет TX-префикс либо 12-байтовый заголовок BL с sequence.</summary>
    public static byte[] Wrap(DroneProfile profile, byte[] payload, uint sequence)
    {
        if (profile == DroneProfile.Tx)
        {
            return [3, .. payload];
        }

        var packet = new byte[12 + payload.Length];
        packet[0] = 0xef;
        packet[1] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), checked((ushort)payload.Length));
        payload.CopyTo(packet, 12);
        return packet;
    }

    public static byte[] Heartbeat(DroneProfile profile) =>
        profile == DroneProfile.Tx ? [1, 1] : [0xef, 0, 4, 0];

    /// <summary>Проверяет только BL-маркер и длину. Полезную нагрузку проверяет её потребитель.</summary>
    public static bool ValidBl(ReadOnlySpan<byte> packet) =>
        packet.Length >= 4 &&
        packet[0] == 0x93 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]) == packet.Length;
}
