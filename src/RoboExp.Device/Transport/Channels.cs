using System.Net;

namespace RoboExp.Device.Transport;

/// <summary>One open HID device exchanging 64-byte reports (report-id byte stripped, like HidDevice.Read/Write).</summary>
internal interface IHidChannel : IDisposable
{
    /// <summary>Writes a 64-byte payload. False on I/O failure.</summary>
    bool Write(byte[] payload);
    /// <summary>Reads one 64-byte payload, or null on timeout/I/O failure.</summary>
    byte[]? Read(TimeSpan timeout);
}

/// <summary>An open serial (SPP) port.</summary>
internal interface ISerialChannel : IDisposable
{
    void Write(byte[] data);
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for <paramref name="count"/> bytes and returns what arrived
    /// (possibly fewer, possibly empty). Never returns more than <paramref name="count"/>.
    /// </summary>
    byte[] Read(int count, TimeSpan timeout);
    void DiscardInput();
}

/// <summary>UDP socket bound to the original local port 36803.</summary>
internal interface IUdpChannel : IDisposable
{
    void Send(byte[] datagram, IPEndPoint to);
    /// <summary>Next datagram or null on timeout.</summary>
    (byte[] Data, IPEndPoint From)? Receive(TimeSpan timeout);
}

/// <summary>Injectable sleep so protocol tests run without wall-clock delays.</summary>
internal delegate Task DelayFunc(TimeSpan delay, CancellationToken ct);

internal static class Delays
{
    public static readonly DelayFunc Real = (d, ct) => Task.Delay(d, ct);
    public static readonly DelayFunc None = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
}
