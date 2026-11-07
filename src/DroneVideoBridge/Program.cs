using System.Net;
using System.Net.Sockets;
using System.Text;
using DroneControl;

// Video-only bridge: no calls to Control(), no flight packets.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
int seconds = 0;
if (args.Length == 2 && args[0] == "--seconds")
{
    seconds = int.Parse(args[1]);
}

if (seconds > 0)
{
    shutdown.CancelAfter(TimeSpan.FromSeconds(seconds));
}

var frameAssembler = new BlFrames();
byte[]? latestFrame = null;
long frameCount = 0;
await using var drone = new DroneLink(DroneProfile.Bl, "192.168.169.1", 8800, false);
drone.Reply += packet =>
{
    var jpeg = frameAssembler.Push(packet);
    if (jpeg != null)
    {
        Interlocked.Exchange(ref latestFrame, jpeg);
        var receivedFrameCount = Interlocked.Increment(ref frameCount);
        if (receivedFrameCount == 1)
        {
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "first-frame.jpg"), jpeg);
            Console.WriteLine($"First JPEG: {jpeg.Length} bytes");
        }
    }

    return frameAssembler.Acknowledgement;
};
drone.Error += e => Console.WriteLine("UDP: " + e);
var server = new TcpListener(IPAddress.Loopback, 8088);
server.Start();
Console.WriteLine("VLC: http://127.0.0.1:8088/video.mjpg (video only)");
var clients = new List<Task>();
async Task ServeClientAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            var stream = client.GetStream();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync(deadline.Token);
            if (request == null || !request.StartsWith("GET /video.mjpg "))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 404 Not Found\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n"), shutdown.Token);
                return;
            }

            int headerBytes = request.Length;
            string? line;
            while ((line = await reader.ReadLineAsync(deadline.Token)) is { Length: > 0 })
            {
                headerBytes += line.Length;
                if (headerBytes > 8192)
                {
                    return;
                }
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n"), shutdown.Token);
            byte[]? previousFrame = null;
            while (!shutdown.IsCancellationRequested)
            {
                var jpeg = Volatile.Read(ref latestFrame);
                if (jpeg != null && !ReferenceEquals(previousFrame, jpeg))
                {
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    writeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "--frame\r\n" +
                        "Content-Type: image/jpeg\r\n" +
                        $"Content-Length: {jpeg.Length}\r\n\r\n"), writeTimeout.Token);
                    await stream.WriteAsync(jpeg, writeTimeout.Token);
                    await stream.WriteAsync("\r\n"u8.ToArray(), writeTimeout.Token);
                    previousFrame = jpeg;
                }

                await Task.Delay(40, shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
        }
    }
}

var acceptClientsTask = Task.Run(async () =>
{
    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            var client = await server.AcceptTcpClientAsync(shutdown.Token);
            clients.RemoveAll(t => t.IsCompleted);
            if (clients.Count >= 4)
            {
                client.Dispose();
            }
            else
            {
                clients.Add(ServeClientAsync(client));
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
});
try
{
    using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(110));
    int heartbeatCount = 0;
    do
    {
        await drone.Send(Protocol.Heartbeat(DroneProfile.Bl));
        if (++heartbeatCount % 45 == 0)
        {
            Console.WriteLine($"Packets={drone.Received} Frames={Interlocked.Read(ref frameCount)}");
        }
    }
    while (await heartbeatTimer.WaitForNextTickAsync(shutdown.Token));
}
catch (OperationCanceledException)
{
}
finally
{
    shutdown.Cancel();
    server.Stop();
    await acceptClientsTask;
    await Task.WhenAll(clients);
    Console.WriteLine($"Stopped. Packets={drone.Received} Frames={frameCount}");
}

