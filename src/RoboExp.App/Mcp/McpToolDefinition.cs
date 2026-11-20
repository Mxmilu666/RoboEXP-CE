using System.Text.Json;

namespace RoboExp.App.Mcp;

public sealed record McpToolDefinition(string Name, string Description, JsonElement InputSchema, bool ReadOnly);
