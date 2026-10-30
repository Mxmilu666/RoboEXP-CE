using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using HidSharp;

namespace RoboExp.Device.Transport;

/// <summary>HidSharp-backed channel. Reports carry report id 0 in byte 0, stripped/added here like HidDevice.Read/Write.</summary>
internal sealed class HidSharpChannel(HidStream stream, int outputLength, int inputLength) : IHidChannel
{
    public static HidSharpChannel? TryOpen(HidDevice device)
    {
        if (!device.TryOpen(out var stream)) return null;
        return new HidSharpChannel(stream, device.GetMaxOutputReportLength(), device.GetMaxInputReportLength());
    }

    public bool Write(byte[] payload)
    {
        var report = new byte[outputLength];
        payload.AsSpan(0, Math.Min(payload.Length, outputLength - 1)).CopyTo(report.AsSpan(1));
        try { stream.Write(report); return true; }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { return false; }
    }

    public byte[]? Read(TimeSpan timeout)
    {
        var report = new byte[inputLength];
        try
        {
            stream.ReadTimeout = (int)timeout.TotalMilliseconds;
            var n = stream.Read(report, 0, report.Length);
            if (n <= 0) return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { return null; }
        var payload = new byte[64];
        report.AsSpan(1, Math.Min(64, report.Length - 1)).CopyTo(payload);
        return payload;
    }

    public void Dispose() => stream.Dispose();
}

internal sealed class SerialPortChannel : ISerialChannel
{
    private readonly SerialPort _port;

    /// <summary>Opens <paramref name="name"/> with the original MSComm/SerialPort settings: baud,n,8,1, no handshake.</summary>
    public SerialPortChannel(string name, int baud, bool dtr)
    {
        _port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = dtr,
            RtsEnable = false,
            ReadTimeout = 500,
            WriteTimeout = 2000,
        };
        _port.Open();
    }

    public void Write(byte[] data) => _port.Write(data, 0, data.Length);

    public byte[] Read(int count, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (_port.BytesToRead < count && sw.Elapsed < timeout)
            Thread.Sleep(10);
        var n = Math.Min(count, _port.BytesToRead);
        var buf = new byte[n];
        var read = 0;
        while (read < n)
            read += _port.Read(buf, read, n - read);
        return buf;
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Dispose() => _port.Dispose();
}

internal sealed class UdpClientChannel : IUdpChannel
{
    private readonly UdpClient _udp;

    public UdpClientChannel(int localPort)
    {
        _udp = new UdpClient(localPort) { EnableBroadcast = true };
    }

    public void Send(byte[] datagram, IPEndPoint to) => _udp.Send(datagram, datagram.Length, to);

    public (byte[] Data, IPEndPoint From)? Receive(TimeSpan timeout)
    {
        _udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        try
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var data = _udp.Receive(ref from);
            return (data, from);
        }
        catch (SocketException) { return null; }
    }

    public void Dispose() => _udp.Dispose();
}
