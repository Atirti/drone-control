using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DroneControl;

/// <summary>BL UDP-транспорт. Обработчик пакета вызывается последовательно из цикла приёма.</summary>
public sealed class DroneLink : IAsyncDisposable
{
    private readonly UdpClient socket;
    private readonly IPEndPoint remoteEndpoint;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task receiveTask;
    private long lastReceiveTimestamp;
    private long receivedPackets;
    private uint commandSequence;
    private Task? disposeTask;

    public long Received => Interlocked.Read(ref receivedPackets);

    /// <summary>Полётная отправка уже начиналась, поэтому при отключении нужна попытка нейтрали.</summary>
    public bool HasControlActivity { get; private set; }

    public bool Responding
    {
        get
        {
            long timestamp = Interlocked.Read(ref lastReceiveTimestamp);
            return !receiveTask.IsCompleted && timestamp > 0 &&
                   Stopwatch.GetElapsedTime(timestamp).TotalSeconds < 3;
        }
    }

    public event Action<string>? Error;

    /// <summary>Единственный синхронный обработчик, возвращающий ACK или null.</summary>
    public Func<byte[], byte[]?>? Reply { get; set; }

    public DroneLink(string host, int port)
    {
        remoteEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        // Connect отфильтрует видео: запросы идут на 8800, ответы приходят с 1234.
        socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        receiveTask = ReceiveAsync();
    }

    public async Task Send(byte[] data) =>
        await socket.SendAsync(data, remoteEndpoint, shutdown.Token);

    public async Task Control(byte[] payload)
    {
        // Сброс ввода может произойти во время await первой отправки.
        HasControlActivity = true;
        await Send(Protocol.Wrap(payload, ++commandSequence));
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(shutdown.Token);
                if (!result.RemoteEndPoint.Address.Equals(remoteEndpoint.Address) ||
                    !Protocol.ValidBl(result.Buffer))
                {
                    continue;
                }

                Interlocked.Exchange(ref lastReceiveTimestamp, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref receivedPackets);
                var reply = Reply?.Invoke(result.Buffer);
                if (reply != null)
                {
                    await Send(reply);
                }

                // Подтверждение сообщения BL, не полётная команда.
                if (result.Buffer.Length >= 12 && result.Buffer[1] == 3)
                {
                    await Send([0xef, 8, 8, 0, .. result.Buffer[4..8]]);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref lastReceiveTimestamp, 0);
            Error?.Invoke(ex.Message);
        }
    }

    public ValueTask DisposeAsync() => new(disposeTask ??= CloseAsync());

    private async Task CloseAsync()
    {
        shutdown.Cancel();
        socket.Dispose();
        try
        {
            await receiveTask;
        }
        finally
        {
            shutdown.Dispose();
        }
    }
}
