using System.Buffers.Binary;
using System.IO;

namespace DroneControl;

/// <summary>
/// Собирает BL-видеокадры из UDP-фрагментов. Вызывать последовательно из потока приёма.
/// Неполные кадры не выдаются; поддерживается не более четырёх кадров одновременно.
/// </summary>
public sealed class BlFrames
{
    private const int FragmentHeaderLength = 56;
    private const int FragmentPayloadLength = 1024;
    private const int MaximumFrameSize = 1024 * 1024;
    private const int MaximumPendingFrames = 4;
    private const int JpegHeaderLength = 605;

    private sealed record Frame(
        int Size,
        int Count,
        int Width,
        int Height,
        int Quality,
        byte[] Data,
        bool[] Seen)
    {
        public int Received;
    }

    private readonly Dictionary<ulong, Frame> frames = [];

    /// <summary>ACK последнего фрагмента или null, если отправка сейчас не нужна.</summary>
    public byte[]? Acknowledgement
    {
        get;
        private set;
    }

    /// <summary>Возвращает полный JPEG либо null при неполном или некорректном кадре.</summary>
    public byte[]? Push(byte[] packet)
    {
        Acknowledgement = null;
        if (!Protocol.ValidBl(packet) || packet.Length < FragmentHeaderLength || packet[1] != 1)
        {
            return null;
        }

        var header = packet.AsSpan();
        ulong frameId = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[44..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[46..]);
        int quality = packet[48];

        if (size <= 0 || size > MaximumFrameSize || count <= 0 || count > 1025 ||
            index < 0 || index >= count || width < 1 || width > 4096 || height < 1 || height > 4096)
        {
            return null;
        }

        // Повторяем формат native-библиотеки: последний фрагмент содержит остаток size % 1024.
        int payloadLength = index == count - 1 ? size % FragmentPayloadLength : FragmentPayloadLength;
        if (payloadLength > packet.Length - FragmentHeaderLength ||
            index * FragmentPayloadLength + payloadLength > size ||
            count != size / FragmentPayloadLength + 1)
        {
            return null;
        }

        if (!frames.TryGetValue(frameId, out var frame))
        {
            if (frames.Count >= MaximumPendingFrames)
            {
                frames.Remove(frames.Keys.Min());
            }

            frame = new Frame(size, count, width, height, quality, new byte[size], new bool[count]);
            frames[frameId] = frame;
        }

        if (frame.Size != size || frame.Count != count || frame.Width != width ||
            frame.Height != height || frame.Quality != quality)
        {
            return null;
        }

        if (!frame.Seen[index])
        {
            header.Slice(FragmentHeaderLength, payloadLength)
                .CopyTo(frame.Data.AsSpan(index * FragmentPayloadLength));
            frame.Seen[index] = true;
            frame.Received++;
        }

        if (index % 16 == 0 || index == count - 1 || frame.Received == count)
        {
            Acknowledgement = BuildAcknowledgement(frameId, frame);
        }

        if (frame.Received != count)
        {
            return null;
        }

        frames.Remove(frameId);
        return BuildJpeg(frame);
    }

    private static byte[] BuildAcknowledgement(ulong frameId, Frame frame)
    {
        // Битовая карта округляется до uint32. Последние 8 байт сохраняют native wire layout.
        int maskLength = ((frame.Count + 31) / 32) * 4;
        int recordLength = 16 + maskLength;
        var acknowledgement = new byte[32 + recordLength + 8];
        acknowledgement[0] = 0xef;
        acknowledgement[1] = 2;
        acknowledgement[4] = 2;
        acknowledgement[5] = 2;
        acknowledgement[7] = 1;
        acknowledgement[8] = 1;

        BinaryPrimitives.WriteUInt16LittleEndian(acknowledgement.AsSpan(2), (ushort)acknowledgement.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(acknowledgement.AsSpan(32), frameId);
        BinaryPrimitives.WriteInt32LittleEndian(acknowledgement.AsSpan(40), frame.Received == frame.Count ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(acknowledgement.AsSpan(44), recordLength);

        for (int index = 0; index < frame.Count; index++)
        {
            if (frame.Seen[index])
            {
                acknowledgement[48 + index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return acknowledgement;
    }

    private static byte[]? BuildJpeg(Frame frame)
    {
        // Некоторые пакеты уже содержат JPEG с маркером SOI.
        if (frame.Quality == 0 && frame.Data.Length > 2 && frame.Data[0] == 255 && frame.Data[1] == 216)
        {
            return frame.Data;
        }

        if (frame.Quality is not (5 or 10 or 25 or 50 or 75 or 100))
        {
            return null;
        }

        string resourceName = $"DroneControl.Headers.q{frame.Quality}.bin";
        using var resource = typeof(BlFrames).Assembly.GetManifestResourceStream(resourceName);
        if (resource == null)
        {
            return null;
        }

        byte[] jpegHeader = new byte[JpegHeaderLength];
        resource.ReadExactly(jpegHeader);
        // В JPEG размеры big endian, в UDP-заголовке — little endian.
        BinaryPrimitives.WriteUInt16BigEndian(jpegHeader.AsSpan(145), (ushort)frame.Height);
        BinaryPrimitives.WriteUInt16BigEndian(jpegHeader.AsSpan(147), (ushort)frame.Width);
        return [.. jpegHeader, .. frame.Data, 255, 217];
    }
}
