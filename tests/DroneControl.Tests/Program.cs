using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DroneControl;

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
    Check(Convert.ToHexString(Protocol.Wrap(DroneProfile.Tx, Protocol.Flight(), 1)) == "036680808080000099", "TX wrapper");
    Check(Convert.ToHexString(Protocol.Wrap(DroneProfile.Bl, Protocol.Flight(), 1)) == "EF04140001000000080000006680808080000099", "BL native wrapper");
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

async Task VerifyTransportAsync()
{
    using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
    await using (var link = new DroneLink(DroneProfile.Tx, "127.0.0.1", port, false))
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Packet += packet => received.TrySetResult(packet);
        await link.Send(Protocol.Heartbeat(DroneProfile.Tx));
        var datagram = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(datagram.Buffer.SequenceEqual(new byte[] { 1, 1 }), "UDP heartbeat loopback");
        await server.SendAsync(new byte[] { 24, 0 }, datagram.RemoteEndPoint);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(link.Responding, "UDP reply enables response status");
        await Task.Delay(3100);
        Check(!link.Responding, "stale reply timeout");
    }

    await using (var simulation = new DroneLink(DroneProfile.Bl, "127.0.0.1", port, true))
    {
        await simulation.Control(Protocol.Flight());
        Check(simulation.Sent == 1 && server.Available == 0, "simulation does not send UDP");
    }

    await using (var bl = new DroneLink(DroneProfile.Bl, "127.0.0.1", port, false))
    {
        var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bl.Packet += packet => reply.TrySetResult(packet);
        await bl.Send(Protocol.Heartbeat(DroneProfile.Bl));
        var datagram = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using var videoPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await videoPort.SendAsync(new byte[] { 147, 1, 4, 0 }, datagram.RemoteEndPoint);
        await reply.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(bl.Received == 1, "BL receives video from different source port");
    }
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

