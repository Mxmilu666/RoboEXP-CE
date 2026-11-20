using System.Text.Json;
using Avalonia.Threading;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Mcp;

public sealed class McpController(MainWindowViewModel workspace) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ConfigurationJson = new() { WriteIndented = true };

    private McpPipeHost? _host;
    private readonly SemaphoreSlim _requests = new(1, 1);
    public bool IsEnabled => _host != null;
    public event EventHandler? Changed;

    public void Start()
    {
        if (_host != null) return;
        var tools = new McpWorkspace(workspace);
        McpPipeHost? host = null;
        host = new McpPipeHost(tools.Tools, async (name, args, ct) =>
        {
            await _requests.WaitAsync(ct);
            try
            {
                return await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_host, host)) throw new McpToolException("MCP access has been disabled.");
                    if (workspace.Dialogs is Avalonia.Controls.Window window && window.OwnedWindows.Any(w => w.IsVisible && w is not McpConnectionWindow))
                        throw new McpToolException("Close the app's dialog before accessing documents.");
                    try { return await tools.CallAsync(name, args, ct); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
                    { throw new McpToolException(e.Message); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
                    { throw new McpToolException("The document could not be saved. Check its location, permissions and character encoding; edits remain in the app."); }
                });
            }
            finally { _requests.Release(); }
        });
        _host = host;
        try { host.Start(); }
        catch { _host = null; throw; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal (string Prompt, string Json) ConnectionDetails
    {
        get
        {
            if (_host == null) return ("", "");
            var command = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate RoboEXP executable.");
            var args = new List<string>();
            if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                args.Add(typeof(McpController).Assembly.Location);
            args.AddRange(["--mcp", "--pipe", _host.PipeName, "--token", _host.Token]);
            var configuration = new { mcpServers = new { roboexp = new { command, args } } };
            return (McpConnectionPrompt.Create(command, args), JsonSerializer.Serialize(configuration, ConfigurationJson));
        }
    }

    public async ValueTask DisposeAsync()
    {
        var host = _host;
        _host = null;
        if (host != null) await host.DisposeAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
