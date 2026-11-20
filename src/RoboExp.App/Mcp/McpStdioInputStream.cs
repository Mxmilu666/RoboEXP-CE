namespace RoboExp.App.Mcp;

// The SDK owns JSON-RPC framing and MCP semantics. This stream only bounds individual input
// lines before its StreamReader can allocate an arbitrarily large JSON-RPC message.
internal sealed class McpStdioInputStream(Stream input) : Stream
{
    private int _lineBytes;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = input.Read(buffer, offset, count);
        CheckLines(buffer.AsSpan(offset, read));
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Standard handles do not consistently honor cancellation on all platforms.
        var pending = input.ReadAsync(buffer, cancellationToken);
        int read = pending.IsCompletedSuccessfully
            ? pending.Result
            : await pending.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        CheckLines(buffer.Span[..read]);
        return read;
    }

    private void CheckLines(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value == '\n')
                _lineBytes = 0;
            else if (++_lineBytes > McpPipeProtocol.MaxMessageBytes)
                throw new IOException("The MCP input exceeds the 2 MiB message limit.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            input.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
