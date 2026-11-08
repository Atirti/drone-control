using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DroneControl;
using DroneVideoBridge;
using System.Text;

int checks = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception(name);
    }

    Console.WriteLine("PASS " + name);
    checks++;
}

VerifyProtocol();
VerifyFlightInput();
VerifyGamepadInput();
await VerifyHttpRequestsAsync();

if (!args.Contains("--input-only"))
{
    await VerifyTransportAsync();
    VerifyFrameAssembly();
}

Console.WriteLine($"{checks} checks passed");

void VerifyProtocol()
{
    Check(Convert.ToHexString(Protocol.Flight()) == "6680808080000099", "APK neutral vector");
    Check(Convert.ToHexString(Protocol.Flight(flags: FlightFlags.Takeoff)) == "6680808080010199", "APK takeoff vector");
    Check(Convert.ToHexString(Protocol.Flight(throttle: 0, flags: FlightFlags.Stop)) == "6680800080048499", "emergency vector");
    Check(Convert.ToHexString(Protocol.Wrap(Protocol.Flight(), 1)) == "EF04140001000000080000006680808080000099", "BL native wrapper");
    Check(
        Protocol.Flight(-3, 500, 1, 0).SequenceEqual(new byte[] { 102, 1, 255, 0, 1, 0, 255, 153 }),
        "clamps and throttle special case");

    for (int flags = 0; flags < 256; flags++)
    {
        var packet = Protocol.Flight(24, 56, 78, 90, (FlightFlags)flags);
        if (packet[6] != (packet[1] ^ packet[2] ^ packet[3] ^ packet[4] ^ packet[5]))
        {
            throw new Exception("checksum");
        }
    }

    Check(true, "all flag combinations checksum");
}

void VerifyFlightInput()
{
    var input = new FlightInput();
    var mappings = new[]
    {
        (false, new StickPosition(0, 1), 2, 1),
        (false, new StickPosition(0, -1), 2, -1),
        (false, new StickPosition(1, 0), 1, 1),
        (false, new StickPosition(-1, 0), 1, -1),
        (true, new StickPosition(0, 1), 3, 1),
        (true, new StickPosition(0, -1), 3, -1),
        (true, new StickPosition(1, 0), 4, 1),
        (true, new StickPosition(-1, 0), 4, -1)
    };

    foreach (var (left, position, axis, sign) in mappings)
    {
        if (left)
        {
            input.SetLeft(position);
        }
        else
        {
            input.SetRight(position);
        }

        var packet = input.Packet(30);
        bool otherAxesAreNeutral = Enumerable.Range(1, 4)
            .Where(index => index != axis)
            .All(index => packet[index] == 128);
        Check(packet[axis] == 128 + sign * 38 && otherAxesAreNeutral, $"stick axis {axis}, sign {sign}");
        input.Release();
        Check(input.Packet(30).SequenceEqual(Protocol.Flight()), $"release axis {axis}, sign {sign}");
    }

    input.SetRight(new StickPosition(1, 0));
    Check(input.Packet(100)[1] == 255 && input.Packet(10)[1] == 141, "speed slider range");
    input.SetRight(new StickPosition(0.5, 0));
    Check(input.Packet(100)[1] == 192 && input.Packet(30)[1] == 147, "proportional stick intensity");
    input.SetRight(new StickPosition(-1, 0));
    Check(input.Packet(100)[1] == 1, "negative axis endpoint");
    input.SetLeft(new StickPosition(0, -1));
    Check(input.Packet(100)[3] == 0, "full downward throttle retains protocol special case");

    input.SetLeft(new StickPosition(0.6, 0.8));
    input.SetRight(new StickPosition(-0.6, -0.8));
    Check(input.Packet(100).SequenceEqual(Protocol.Flight(52, 26, 230, 204)), "four simultaneous analog axes");
    input.SetLeft(default);
    Check(input.Left.IsCentered && input.Packet(100)[1] == 52, "releasing left keeps right stick");
    input.Release();
    Check(input.Left.IsCentered && input.Right.IsCentered, "reset clears both sticks");

    var diagonal = StickPosition.Normalize(3, 4);
    Check(Math.Abs(diagonal.X - 0.6) < 0.00001 && Math.Abs(diagonal.Y - 0.8) < 0.00001, "circular clamp preserves direction");
    Check(StickPosition.Normalize(double.NaN, 1).IsCentered, "nonfinite input resets to neutral");
}

void VerifyGamepadInput()
{
    Check(GamepadInput.Normalize(0, 0).IsCentered, "gamepad neutral");
    Check(GamepadInput.Normalize(5000, -1000).IsCentered, "gamepad drift suppressed");
    Check(GamepadInput.Normalize(short.MaxValue, 0).X == 1, "SDL right endpoint");
    Check(GamepadInput.Normalize(short.MinValue, 0).X == -1, "SDL left endpoint");
    Check(GamepadInput.Normalize(0, short.MinValue).Y == 1, "SDL up reverses Y");
    Check(GamepadInput.Normalize(0, short.MaxValue).Y == -1, "SDL down reverses Y");
    Check(GamepadInput.Normalize(5898, 0).IsCentered, "dead zone inner boundary");
    var edge = GamepadInput.Normalize(5899, 0);
    Check(edge.X > 0 && edge.X < 0.001, "dead zone continuous outer boundary");
    var half = GamepadInput.Normalize(16384, 0);
    Check(half.X > 0.39 && half.X < 0.40, "remaining stick travel scaled proportionally");
    var diagonal = GamepadInput.Normalize(short.MaxValue, short.MinValue);
    Check(Math.Abs(diagonal.X - Math.Sqrt(0.5)) < 0.00001 && diagonal.Y == diagonal.X,
        "gamepad diagonal bounded to unit circle");

    var input = new FlightInput();
    input.SetLeft(GamepadInput.Normalize(short.MaxValue, 0));
    input.SetRight(GamepadInput.Normalize(0, short.MinValue));
    Check(input.Packet(100).SequenceEqual(Protocol.Flight(pitch: 255, yaw: 255)),
        "SDL sticks mapped to simultaneous yaw and pitch");

    var gate = new GamepadInput();
    var centered = new GamepadState(1, "Test", default, default);
    var moving = centered with { Left = new StickPosition(1, 0) };
    bool Update(GamepadState? state, int milliseconds) => gate.Update(state, TimeSpan.FromMilliseconds(milliseconds));

    Check(!Update(moving, 0), "held stick blocked on activation");
    Check(!Update(centered, 100) && !Update(centered, 399), "neutral must last 300 ms");
    Check(Update(centered, 400) && Update(moving, 450), "center unlocks movement");
    Check(!Update(null, 500) && !gate.IsReady, "disconnect closes input gate");
    Check(!Update(moving, 600), "held stick blocked after reconnect");
    Check(!Update(centered, 700) && !Update(moving, 900) && !Update(centered, 1000) &&
          !Update(centered, 1299) && Update(centered, 1300), "interrupted centering restarts timer");
    Check(!Update(centered with { DeviceId = 2 }, 1400) &&
          !Update(moving with { DeviceId = 2 }, 1500), "replacement requires new centering");
    gate.Reset();
    Check(!Update(centered, 2000) && !Update(centered, 2299) && Update(centered, 2300),
        "focus or source reset requires full centering");
}

async Task VerifyTransportAsync()
{
    using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
    await using (var link = new DroneLink("127.0.0.1", port))
    {
        Check(server.Available == 0 && !link.HasControlActivity, "opening link sends no commands");
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Reply = packet =>
        {
            received.TrySetResult(packet);
            return null;
        };
        await link.Send(Protocol.Heartbeat());
        var datagram = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(datagram.Buffer.SequenceEqual(new byte[] { 0xef, 0, 4, 0 }), "BL heartbeat loopback");
        // Видеопорт отличается от порта, на который клиент отправляет запросы.
        using var videoPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await videoPort.SendAsync(new byte[] { 0x93, 1, 4, 0 }, datagram.RemoteEndPoint);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(link.Responding && link.Received == 1, "BL accepts reply from a different source port");
        await Task.Delay(3100);
        Check(!link.Responding, "stale reply timeout");

        var control = link.Control(Protocol.Flight());
        Check(link.HasControlActivity, "control activity visible before awaiting send");
        await control;
        var command = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(Convert.ToHexString(command.Buffer) == "EF04140001000000080000006680808080000099",
            "real transport wraps first command with sequence 1");
    }

    var failingLink = new DroneLink("127.0.0.1", port);
    try
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        failingLink.Error += message => failed.TrySetResult(message);
        failingLink.Reply = _ => throw new InvalidOperationException("test receive failure");
        await failingLink.Send(Protocol.Heartbeat());
        var datagram = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await server.SendAsync(new byte[] { 0x93, 1, 4, 0 }, datagram.RemoteEndPoint);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!failingLink.Responding, "receive failure immediately clears response status");
    }
    finally
    {
        await failingLink.DisposeAsync();
        await failingLink.DisposeAsync();
    }
    Check(!failingLink.Responding, "repeated disposal is safe and link stays inactive");
}

async Task VerifyHttpRequestsAsync()
{
    using var valid = new FragmentedStream(Encoding.ASCII.GetBytes("GET /video.mjpg HTTP/1.1\r\nHost: localhost\r\n\r\n"));
    Check(await MjpegRequest.IsVideoRequestAsync(valid, CancellationToken.None), "fragmented HTTP headers accepted");
    using var wrongPath = new MemoryStream("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
    Check(!await MjpegRequest.IsVideoRequestAsync(wrongPath, CancellationToken.None), "other HTTP path rejected");
    using var incomplete = new MemoryStream("GET /video.mjpg HTTP/1.1\r\nHost: local"u8.ToArray());
    Check(!await MjpegRequest.IsVideoRequestAsync(incomplete, CancellationToken.None), "truncated HTTP headers rejected");
    using var oversized = new MemoryStream(Enumerable.Repeat((byte)'A', 10000).ToArray());
    Check(!await MjpegRequest.IsVideoRequestAsync(oversized, CancellationToken.None) && oversized.Position == 8192,
        "unterminated HTTP line cannot read beyond 8 KiB");
}

void VerifyFrameAssembly()
{
    Check(!Protocol.ValidBl(new byte[] { 147, 1, 5, 0 }), "reject inconsistent BL length");
    var assembler = new BlFrames();
    for (int sample = 0; sample < 1500; sample++)
    {
        var packet = new byte[sample % 1200];
        Random.Shared.NextBytes(packet);
        assembler.Push(packet);
    }

    Check(true, "malformed video datagrams do not crash");
    byte[] CreateFragment(int index)
    {
        // Один кадр: 1024 байта в первом фрагменте и 4 байта во втором.
        var packet = new byte[index == 0 ? 1080 : 60];
        packet[0] = 147;
        packet[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), 7);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(36), 2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(40), 1028);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(44), 640);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(46), 360);
        if (index == 0)
        {
            packet[56] = 255;
            packet[57] = 216;
        }

        return packet;
    }

    Check(assembler.Push(CreateFragment(1)) == null, "incomplete frame withheld");
    Check(assembler.Push(CreateFragment(1)) == null, "duplicate fragment ignored");
    Check(assembler.Push(CreateFragment(0))?.Length == 1028, "out-of-order frame assembly");
    Check(
        assembler.Acknowledgement is { Length: 60 } ack &&
        ack[0] == 239 && ack[1] == 2 && ack[8] == 1 && ack[40] == 1 && ack[48] == 3,
        "BL complete-frame ACK");
}

sealed class FragmentedStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
}
