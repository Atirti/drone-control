using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DroneControl;

/// <summary>UDP-транспорт. События вызываются из цикла приёма, без отдельной диспетчеризации.</summary>
public sealed class DroneLink : IAsyncDisposable
{
    private readonly UdpClient? socket;
    private readonly IPEndPoint remoteEndpoint;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task receiveTask;
    private long lastReceiveTimestamp;
    private uint commandSequence;

    public DroneProfile Profile
    {
        get;
    }
    public bool Simulation
    {
        get;
    }
    public long Sent
    {
        get;
        private set;
    }
    public long Received
    {
        get;
        private set;
    }
    public long ControlsSent
    {
        get;
        private set;
    }

    /// <summary>Есть принятый пакет не старше трёх секунд либо включена симуляция.</summary>
    public bool Responding => Simulation ||
        (Interlocked.Read(ref lastReceiveTimestamp) > 0 &&
         Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReceiveTimestamp)).TotalSeconds < 3);

    public event Action<byte[]>? Packet;
    public event Action<string>? Error;

    /// <summary>Единственный синхронный обработчик, возвращающий ACK или null.</summary>
    public event Func<byte[], byte[]?>? Reply;

    public DroneLink(DroneProfile profile, string host, int port, bool simulation)
    {
        Profile = profile;
        Simulation = simulation;
        remoteEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
        if (!simulation)
        {
            // Connect отфильтрует BL-видео: запросы идут на 8800, ответы приходят с 1234.
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }

        receiveTask = simulation ? Task.CompletedTask : ReceiveAsync();
    }

    public async Task Send(byte[] data)
    {
        if (socket != null)
        {
            await socket.SendAsync(data, remoteEndpoint, shutdown.Token);
        }

        Sent++;
    }

    public async Task Control(byte[] payload)
    {
        await Send(Protocol.Wrap(Profile, payload, ++commandSequence));
        ControlsSent++;
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var result = await socket!.ReceiveAsync(shutdown.Token);
                if (!result.RemoteEndPoint.Address.Equals(remoteEndpoint.Address))
                {
                    continue;
                }

                if (Profile == DroneProfile.Tx && result.RemoteEndPoint.Port != remoteEndpoint.Port)
                {
                    continue;
                }

                if (Profile == DroneProfile.Bl && !Protocol.ValidBl(result.Buffer))
                {
                    continue;
                }

                if (result.Buffer.Length == 0)
                {
                    continue;
                }

                Interlocked.Exchange(ref lastReceiveTimestamp, Stopwatch.GetTimestamp());
                Received++;
                Packet?.Invoke(result.Buffer);

                var reply = Reply?.Invoke(result.Buffer);
                if (reply != null)
                {
                    await Send(reply);
                }

                // Подтверждение входящего управляющего сообщения BL; это не полётная команда.
                if (Profile == DroneProfile.Bl && result.Buffer.Length >= 12 && result.Buffer[1] == 3)
                {
                    await Send([0xef, 8, 8, 0, .. result.Buffer[4..8]]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // DisposeAsync отменяет ожидающий ReceiveAsync.
        }
        catch (ObjectDisposedException)
        {
            // Сокет закрывается при отключении.
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        socket?.Dispose();
        await receiveTask;
        shutdown.Dispose();
    }
}
