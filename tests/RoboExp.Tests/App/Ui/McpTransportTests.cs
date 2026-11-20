using System.IO.Pipes;
using System.Text.Json;
using RoboExp.App.Mcp;

namespace RoboExp.Tests.App.Ui;

public sealed class McpTransportTests
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly McpToolDefinition[] Tools =
    [
        new("read_document", "Read the open document.",
            JsonSerializer.SerializeToElement(new { type = "object" }), true)
    ];

    [Fact]
    public async Task CurrentSessionListsSchemasAndCallsTheHandler()
    {
        int calls = 0;
        await using var host = new McpPipeHost(Tools, (name, arguments, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("read_document", name);
            Assert.Equal(42, arguments.GetProperty("documentId").GetInt32());
            Interlocked.Increment(ref calls);
            return Task.FromResult<object?>(new { text = "open editor content" });
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var list = await ExchangeAsync(host, host.Token, "list", Empty, deadline.Token);
        Assert.Null(list.Error);
        var definitions = list.Data!.Value.Deserialize<McpToolDefinition[]>(McpPipeProtocol.JsonOptions)!;
        Assert.Single(definitions);
        Assert.True(definitions[0].ReadOnly);
        Assert.Equal("object", definitions[0].InputSchema.GetProperty("type").GetString());

        var result = await ExchangeAsync(host, host.Token, "call",
            JsonSerializer.SerializeToElement(new { documentId = 42 }), deadline.Token);
        Assert.Null(result.Error);
        Assert.Equal("open editor content", result.Data!.Value.GetProperty("text").GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WrongTokenNeverInvokesHandlerOrReturnsToolList()
    {
        int calls = 0;
        await using var host = new McpPipeHost(Tools, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object?>(null);
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string wrongToken = new('0', 64);
        foreach (string method in new[] { "list", "call" })
        {
            var response = await ExchangeAsync(host, wrongToken, method, Empty, deadline.Token);
            Assert.Contains("Unauthorized", response.Error);
            Assert.Null(response.Data);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDisconnectsAndCancelsPendingDispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutations = 0;
        await using var host = new McpPipeHost(Tools, async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref mutations);
                return null;
            }
            finally { canceled.TrySetResult(); }
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var request = ExchangeAsync(host, host.Token, "call", Empty, requestCancellation.Token);
        await entered.Task.WaitAsync(deadline.Token);
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await canceled.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task DisposalCancelsPendingDispatchAndDeniesSubsequentConnections()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutations = 0;
        await using var host = new McpPipeHost(Tools, async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref mutations);
                return null;
            }
            finally { canceled.TrySetResult(); }
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = ExchangeAsync(host, host.Token, "call", Empty, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        await host.DisposeAsync();
        await canceled.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => request);
        Assert.Equal(0, mutations);
        Assert.Throws<ObjectDisposedException>(host.Start);

        using var disconnected = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => disconnected.ConnectAsync(100));
    }

    [Fact]
    public async Task OnlyExplicitlySafeErrorsReachClients()
    {
        await using var safeHost = new McpPipeHost(Tools, (_, _, _) =>
            throw new McpToolException("Read the latest revision before editing."));
        await using var unexpectedHost = new McpPipeHost(Tools, (_, _, _) =>
            throw new InvalidOperationException("private document path and credentials"));
        safeHost.Start();
        unexpectedHost.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var safe = await ExchangeAsync(safeHost, safeHost.Token, "call", Empty, deadline.Token);
        Assert.Equal("Read the latest revision before editing.", safe.Error);
        var unexpected = await ExchangeAsync(unexpectedHost, unexpectedHost.Token, "call", Empty, deadline.Token);
        Assert.Equal("The document tool could not be completed.", unexpected.Error);
        Assert.Null(unexpected.Data);
    }

    [Fact]
    public async Task SimultaneousClientsHaveIndependentConnections()
    {
        int enteredCount = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new McpPipeHost(Tools, async (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref enteredCount) == 2)
                bothEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new { ok = true };
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = ExchangeAsync(host, host.Token, "call", Empty, deadline.Token);
        var second = ExchangeAsync(host, host.Token, "call", Empty, deadline.Token);
        await bothEntered.Task.WaitAsync(deadline.Token);
        release.SetResult();
        foreach (var response in await Task.WhenAll(first, second))
            Assert.Null(response.Error);
    }

    [Fact]
    public async Task OversizedPipeRequestIsRejectedBeforeDispatch()
    {
        int calls = 0;
        await using var host = new McpPipeHost(Tools, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object?>(null);
        });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        var oversized = new byte[McpPipeProtocol.MaxMessageBytes + 1];
        Array.Fill(oversized, (byte)' ');
        await pipe.WriteAsync(oversized, deadline.Token);
        var json = await McpPipeProtocol.ReadAsync(pipe, deadline.Token);
        var response = json.Deserialize<McpPipeProtocol.Response>(McpPipeProtocol.JsonOptions)!;
        Assert.Contains("2 MiB", response.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StdioInputRejectsOversizedLinesBeforeSdkDeserialization()
    {
        var oversized = new byte[McpPipeProtocol.MaxMessageBytes + 1];
        Array.Fill(oversized, (byte)' ');
        using var input = new McpStdioInputStream(new MemoryStream(oversized));
        var buffer = new byte[8192];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await input.ReadAsync(buffer.AsMemory()) != 0) { }
        });
    }

    [Fact]
    public async Task StdioLimitResetsForEachMessage()
    {
        var messages = new byte[2 * McpPipeProtocol.MaxMessageBytes + 2];
        Array.Fill(messages, (byte)' ');
        messages[McpPipeProtocol.MaxMessageBytes] = (byte)'\n';
        messages[^1] = (byte)'\n';
        using var input = new McpStdioInputStream(new MemoryStream(messages));
        var buffer = new byte[8192];
        int bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory())) != 0)
            bytes += read;
        Assert.Equal(messages.Length, bytes);
    }

    private static Task<McpPipeProtocol.Response> ExchangeAsync(McpPipeHost host, string token,
        string method, JsonElement arguments, CancellationToken cancellationToken) =>
        McpPipeProtocol.ExchangeAsync(host.PipeName,
            new(token, method, method == "call" ? "read_document" : null, arguments), cancellationToken);
}
