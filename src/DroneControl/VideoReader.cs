using System.Diagnostics;

namespace DroneControl;

/// <summary>Резервный TX/RTSP-приём через FFmpeg. В текущем BL-интерфейсе не используется.</summary>
public sealed class VideoReader : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private Process? process;

    public async Task Run(string executable, string host, Action<byte[]> frame, Action<string> log)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner",
            "-loglevel",
            "error",
            "-rtsp_transport",
            "tcp",
            "-i",
            $"rtsp://{host}:7070/webcam",
            "-an",
            "-vf",
            "fps=15,scale=960:-2",
            "-f",
            "image2pipe",
            "-vcodec",
            "mjpeg",
            "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        process = new Process
        {
            StartInfo = startInfo
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                log(e.Data);
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        var buffer = new byte[16384];
        var jpeg = new List<byte>();
        bool isInsideFrame = false;
        byte previousByte = 0;
        try
        {
            int bytesRead;
            while ((bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, shutdown.Token)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    byte currentByte = buffer[i];
                    if (!isInsideFrame && previousByte == 255 && currentByte == 216)
                    {
                        jpeg.Clear();
                        jpeg.Add(255);
                        isInsideFrame = true;
                    }

                    if (isInsideFrame)
                    {
                        jpeg.Add(currentByte);
                        if (previousByte == 255 && currentByte == 217)
                        {
                            frame(jpeg.ToArray());
                            isInsideFrame = false;
                        }
                        else if (jpeg.Count > 8_000_000)
                        {
                            isInsideFrame = false;
                            jpeg.Clear();
                        }
                    }

                    previousByte = currentByte;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (!shutdown.IsCancellationRequested)
        {
            shutdown.Cancel();
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
