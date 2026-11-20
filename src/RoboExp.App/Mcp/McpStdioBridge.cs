using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoboExp.App.Mcp;

/// <summary>Headless MCP entry point. This must run before initializing Avalonia.</summary>
public static class McpStdioBridge
{
    private const string Unavailable = "Local MCP access is disabled or the RoboEXP CE session is no longer available. Ask the user to enable MCP in the app and copy a fresh connection prompt.";
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out string pipeName, out string token))
        {
            await Console.Error.WriteLineAsync("Usage: RoboExp.App --mcp --pipe SESSION_PIPE --token SESSION_TOKEN").ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = new McpServerOptions
            {
                ServerInfo = new() { Name = "RoboEXP CE", Version = "1.0.0" },
                ScopeRequests = false,
                Capabilities = new() { Tools = new() { ListChanged = false } },
                ServerInstructions = "Access is limited to documents already open in the connected RoboEXP CE window. Read the current document revision before editing. Changes appear in the app and are undoable. No shell, robot, network or arbitrary file access is provided.",
                Handlers = new()
                {
                    ListToolsHandler = async (_, cancellationToken) =>
                    {
                        var response = await ExchangeAsync(pipeName, token, "list", null, EmptyArguments, cancellationToken).ConfigureAwait(false);
                        if (response.Error is not null)
                            throw new McpProtocolException(response.Error, McpErrorCode.InternalError);
                        var definitions = response.Data?.Deserialize<McpToolDefinition[]>(McpPipeProtocol.JsonOptions)
                            ?? throw new McpProtocolException("Invalid local MCP tool list.", McpErrorCode.InternalError);
                        return new ListToolsResult
                        {
                            Tools = definitions.Select(definition => new Tool
                            {
                                Name = definition.Name,
                                Description = definition.Description,
                                InputSchema = definition.InputSchema,
                                Annotations = new()
                                {
                                    ReadOnlyHint = definition.ReadOnly,
                                    OpenWorldHint = false,
                                    DestructiveHint = !definition.ReadOnly
                                }
                            }).ToList()
                        };
                    },
                    CallToolHandler = async (request, cancellationToken) =>
                    {
                        if (request.Params is null)
                            return ToolError("A tool name and arguments are required.");
                        var arguments = request.Params.Arguments is null
                            ? EmptyArguments
                            : JsonSerializer.SerializeToElement(request.Params.Arguments, McpPipeProtocol.JsonOptions);
                        var response = await ExchangeAsync(pipeName, token, "call", request.Params.Name, arguments, cancellationToken).ConfigureAwait(false);
                        if (response.Error is not null)
                            return ToolError(response.Error);
                        var result = response.Data ?? JsonSerializer.SerializeToElement<object?>(null);
                        return new CallToolResult
                        {
                            IsError = false,
                            Content = [new TextContentBlock { Text = result.GetRawText() }],
                            StructuredContent = result.ValueKind == JsonValueKind.Object ? result : null
                        };
                    }
                }
            };

            // The official SDK owns all JSON-RPC framing, initialization and MCP handlers.
            // Use inherited handles directly (also for WinExe) with a bounded stdin wrapper.
            // No GUI, console attachment, logger or HTTP transport is initialized here.
            await using var transport = new StreamServerTransport(
                new McpStdioInputStream(Console.OpenStandardInput()),
                new BufferedStream(Console.OpenStandardOutput()), "RoboEXP CE");
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            // Never print exception messages: they may contain pipe names, tokens or document text.
            await Console.Error.WriteLineAsync("The RoboEXP CE MCP stdio session stopped unexpectedly.").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<McpPipeProtocol.Response> ExchangeAsync(string pipeName, string token,
        string method, string? name, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await McpPipeProtocol.ExchangeAsync(pipeName,
                new(token, method, name, arguments), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpToolException exception)
        {
            return new(null, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return new(null, Unavailable);
        }
        catch (Exception)
        {
            return new(null, "Invalid response from the local RoboEXP CE session.");
        }
    }

    private static CallToolResult ToolError(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    private static bool TryParseArguments(string[] args, out string pipeName, out string token)
    {
        pipeName = "";
        token = "";
        bool seenMcp = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mcp" when !seenMcp:
                    seenMcp = true;
                    break;
                case "--pipe" when pipeName.Length == 0 && index + 1 < args.Length:
                    pipeName = args[++index];
                    break;
                case "--token" when token.Length == 0 && index + 1 < args.Length:
                    token = args[++index];
                    break;
                default:
                    return false;
            }
        }

        // A session identifier is not a path. Reject separators and remote pipe names outright.
        return pipeName.StartsWith(McpPipeProtocol.PipePrefix, StringComparison.Ordinal)
            && IsLowerHex(pipeName.AsSpan(McpPipeProtocol.PipePrefix.Length), 16)
            && IsLowerHex(token, 64);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length)
            return false;
        foreach (char character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }
}
