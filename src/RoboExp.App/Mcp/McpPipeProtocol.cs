using System.Buffers;
using System.IO.Pipes;
using System.Text.Json;

namespace RoboExp.App.Mcp;

/// <summary>An error whose message is safe to return to an MCP client.</summary>
public sealed class McpToolException(string message) : Exception(message)
{
}

// One authenticated request and response per connection. Closing the connection cancels the request.
internal static class McpPipeProtocol
{
    internal const int MaxMessageBytes = 2 * 1024 * 1024;
    internal const string PipePrefix = "rxm-";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Newline = [(byte)'\n'];

    internal sealed record Request(string Token, string Method, string? Name, JsonElement Arguments);
    internal sealed record Response(JsonElement? Data, string? Error);

    internal static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var message = new ArrayBufferWriter<byte>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    throw new IOException("The local MCP connection closed.");

                int newline = buffer.AsSpan(0, count).IndexOf((byte)'\n');
                int length = newline < 0 ? count : newline;
                if (message.WrittenCount + length > MaxMessageBytes)
                    throw new McpToolException("The local MCP message exceeds the 2 MiB limit.");
                message.Write(buffer.AsSpan(0, length));
                if (newline < 0)
                    continue;
                if (newline != count - 1)
                    throw new McpToolException("Only one request is allowed per local MCP connection.");

                using var document = JsonDocument.Parse(message.WrittenMemory);
                return document.RootElement.Clone();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        using var buffer = new BoundedMessageStream();
        JsonSerializer.Serialize(buffer, value, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Response> ExchangeAsync(string pipeName, Request request, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Disposal also interrupts an in-flight UI call on the host through its disconnect monitor.
        using var cancellation = cancellationToken.Register(static state => ((NamedPipeClientStream)state!).Dispose(), pipe);
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
            await WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            var json = await ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            return json.Deserialize<Response>(JsonOptions) ?? throw new IOException("Invalid local MCP response.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class BoundedMessageStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(buffer.Length);
            base.Write(buffer);
        }

        private void CheckLength(int count)
        {
            if (Length + count > MaxMessageBytes)
                throw new McpToolException("The local MCP message exceeds the 2 MiB limit. Use a smaller edit or document.");
        }
    }
}
