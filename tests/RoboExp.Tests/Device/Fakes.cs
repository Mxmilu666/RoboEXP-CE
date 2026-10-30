using System.Net;
using RoboExp.Device.Protocol;
using RoboExp.Device.Transport;

namespace RoboExp.Tests.Device;

/// <summary>HID fake: records writes, answers each write via a responder (null = timeout).</summary>
internal sealed class FakeHid(Func<byte[], int, byte[]?> respond) : IHidChannel
{
    public List<byte[]> Writes { get; } = [];
    private byte[]? _pending;
    public bool FailWrites { get; init; }

    public bool Write(byte[] payload)
    {
        if (FailWrites) return false;
        Writes.Add(payload.ToArray());
        _pending = respond(payload, Writes.Count - 1);
        return true;
    }

    public byte[]? Read(TimeSpan timeout)
    {
        var r = _pending;
        _pending = null;
        if (r is null) return null;
        var full = new byte[64];
        r.CopyTo(full, 0);
        return full;
    }

    public void Dispose() { }
}

internal sealed class FakeSerial(Func<byte[], int, byte[]> respond) : ISerialChannel
{
    public List<byte[]> Writes { get; } = [];
    private readonly Queue<byte> _input = new();

    public void Write(byte[] data)
    {
        Writes.Add(data.ToArray());
        foreach (var b in respond(data, Writes.Count - 1)) _input.Enqueue(b);
    }

    public byte[] Read(int count, TimeSpan timeout)
    {
        var n = Math.Min(count, _input.Count);
        var r = new byte[n];
        for (var i = 0; i < n; i++) r[i] = _input.Dequeue();
        return r;
    }

    public void DiscardInput() => _input.Clear();
    public void Dispose() { }
}

internal sealed class FakeUdp(Func<byte[], IPEndPoint, int, byte[]?> respond) : IUdpChannel
{
    public static readonly IPEndPoint Controller = new(IPAddress.Parse("192.168.1.50"), 28000);
    public List<(byte[] Data, IPEndPoint To)> Sent { get; } = [];
    private byte[]? _pending;

    public void Send(byte[] datagram, IPEndPoint to)
    {
        Sent.Add((datagram.ToArray(), to));
        _pending = respond(datagram, to, Sent.Count - 1);
    }

    public (byte[] Data, IPEndPoint From)? Receive(TimeSpan timeout)
    {
        var r = _pending;
        _pending = null;
        return r is null ? null : (r, Controller);
    }

    public void Dispose() { }
}

internal static class TestFiles
{
    public static readonly DateTime Stamp = new(2024, 5, 17, 13, 45, 38);

    public static BinFile Make(int length, string name = "PROJ", Func<int, byte>? content = null)
    {
        content ??= i => (byte)(i * 7 + 3);
        var bytes = Enumerable.Range(0, length).Select(content).ToArray();
        return new BinFile($"/tmp/{name}.bin", bytes, Stamp);
    }
}
