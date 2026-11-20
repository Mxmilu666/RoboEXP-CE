using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoboExp.App.Mcp;

/// <summary>Opt-in, current-user-only access to the running application's document tools.</summary>
public sealed class McpPipeHost : IAsyncDisposable
{
    private const int MaxClients = 32;
    private readonly object _gate = new();
    private readonly IReadOnlyList<McpToolDefinition> _tools;
    private readonly HashSet<string> _toolNames;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _callTool;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<long, Task> _clients = [];
    private readonly byte[] _tokenBytes;
    private NamedPipeServerStream? _listener;
    private Task? _acceptTask;
    private Task? _disposeTask;
    private long _nextClient;
    private bool _started;
    private bool _stopped;

    public string PipeName { get; } = McpPipeProtocol.PipePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public McpPipeHost(IReadOnlyList<McpToolDefinition> tools,
        Func<string, JsonElement, CancellationToken, Task<object?>> callTool)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(callTool);
        _tools = tools.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToArray();
        _toolNames = new(_tools.Select(tool => tool.Name), StringComparer.Ordinal);
        if (_toolNames.Count != _tools.Count)
            throw new ArgumentException("MCP tool names must be unique.", nameof(tools));
        _callTool = callTool;
        _tokenBytes = Encoding.ASCII.GetBytes(Token);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started)
                return;
            _listener = CreateListener();
            _started = true;
            _acceptTask = Task.Run(AcceptAsync);
        }
    }

    private NamedPipeServerStream CreateListener() => new(PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                NamedPipeServerStream listener;
                lock (_gate)
                {
                    if (_stopped)
                        return;
                    listener = _listener!;
                }
                await listener.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_stopped)
                        return;
                    try { _listener = CreateListener(); }
                    catch
                    {
                        listener.Dispose();
                        throw;
                    }
                    if (_clients.Count >= MaxClients)
                    {
                        listener.Dispose();
                        continue;
                    }
                    long id = ++_nextClient;
                    _clients.Add(id, Task.Run(async () =>
                    {
                        try { await HandleClientAsync(listener).ConfigureAwait(false); }
                        finally
                        {
                            listener.Dispose();
                            lock (_gate) { _clients.Remove(id); }
                        }
                    }));
                }
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
        catch (Exception)
        {
            // A failed listener fails closed; the UI can disable and re-enable MCP for a new session.
            lock (_gate)
            {
                _stopped = true;
                _listener?.Dispose();
            }
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var cancellationToken = requestCancellation.Token;
        Task? disconnectMonitor = null;
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var json = await McpPipeProtocol.ReadAsync(pipe, readTimeout.Token).ConfigureAwait(false);
            var request = json.Deserialize<McpPipeProtocol.Request>(McpPipeProtocol.JsonOptions);
            McpPipeProtocol.Response response;
            if (request is null || !IsAuthorized(request.Token))
            {
                response = Error("Unauthorized local MCP session. Use the current configuration shown in RoboEXP CE.");
            }
            else
            {
                // Begin observing disconnect before scheduling any work onto the application UI thread.
                disconnectMonitor = MonitorDisconnectAsync(pipe, requestCancellation);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_stopped || !_started)
                        throw new McpToolException("Local MCP access is disabled.");
                }
                object? data;
                switch (request.Method)
                {
                    case "list":
                        data = _tools;
                        break;
                    case "call":
                        if (request.Name is null || !_toolNames.Contains(request.Name))
                            throw new McpToolException("Unknown document tool.");
                        if (request.Arguments.ValueKind != JsonValueKind.Object)
                            throw new McpToolException("Tool arguments must be a JSON object.");
                        cancellationToken.ThrowIfCancellationRequested();
                        data = await _callTool(request.Name, request.Arguments, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new McpToolException("Unknown local MCP request.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                response = new(JsonSerializer.SerializeToElement(data, McpPipeProtocol.JsonOptions), null);
            }
            await McpPipeProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            string message = exception is McpToolException
                ? exception.Message.Replace(Token, "[redacted]", StringComparison.Ordinal)
                    .Replace(PipeName, "[redacted]", StringComparison.Ordinal)
                : exception is JsonException ? "Invalid local MCP request." : "The document tool could not be completed.";
            if (message.Length > 2000)
                message = message[..2000];
            try { await McpPipeProtocol.WriteAsync(pipe, Error(message), cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* The client may have closed or the host may have stopped. */ }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await requestCancellation.CancelAsync().ConfigureAwait(false);
            if (disconnectMonitor is not null)
                await disconnectMonitor.ConfigureAwait(false);
        }
    }

    private bool IsAuthorized(string? token)
    {
        if (token is null || token.Length != Token.Length)
            return false;
        Span<byte> bytes = stackalloc byte[64];
        int count = Encoding.ASCII.GetBytes(token, bytes);
        return CryptographicOperations.FixedTimeEquals(bytes[..count], _tokenBytes);
    }

    private static McpPipeProtocol.Response Error(string message) => new(null, message);

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource cancellation)
    {
        try
        {
            // Any extra byte is a protocol violation; EOF means cancellation/disconnection.
            await pipe.ReadAsync(new byte[1], cancellation.Token).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new(_disposeTask);
            _stopped = true;
            _listener?.Dispose();
            _disposeTask = Task.Run(DisposeCoreAsync);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptTask is not null)
            await _acceptTask.ConfigureAwait(false);
        Task[] clients;
        lock (_gate) { clients = _clients.Values.ToArray(); }
        await Task.WhenAll(clients).ConfigureAwait(false);
        _shutdown.Dispose();
        CryptographicOperations.ZeroMemory(_tokenBytes);
    }
}
