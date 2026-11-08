using System.Text;

namespace DroneVideoBridge;

/// <summary>Читает только заголовок локального MJPEG-запроса, максимум 8 КиБ.</summary>
public static class MjpegRequest
{
    public static async Task<bool> IsVideoRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8192];
        int length = 0;
        while (length < header.Length)
        {
            int received = await stream.ReadAsync(header.AsMemory(length), cancellationToken);
            if (received == 0)
            {
                return false;
            }

            length += received;
            if (header.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                continue;
            }

            int lineEnd = header.AsSpan(0, length).IndexOf("\r\n"u8);
            string requestLine = Encoding.ASCII.GetString(header, 0, lineEnd);
            return requestLine is "GET /video.mjpg HTTP/1.0" or "GET /video.mjpg HTTP/1.1";
        }

        return false;
    }
}
