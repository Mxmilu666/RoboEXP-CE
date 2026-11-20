using System.Text.Json;
using RoboExp.App.Localization;

namespace RoboExp.App.Mcp;

internal static class McpConnectionPrompt
{
    public static string Create(string executable, IReadOnlyList<string> arguments) =>
        Localizer.Instance.T("Port:Mcp.PromptInstructions") + "\n\n" +
        "ROBOEXP_MCP_ARGV_JSON:\n```json\n" +
        JsonSerializer.Serialize(new[] { executable }.Concat(arguments).ToArray(), new JsonSerializerOptions { WriteIndented = true }) +
        "\n```\n\n" + Localizer.Instance.T("Port:Mcp.PromptSafety");
}
