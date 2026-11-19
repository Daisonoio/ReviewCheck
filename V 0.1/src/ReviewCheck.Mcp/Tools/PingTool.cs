using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ReviewCheck.Mcp.Tools;

/// <summary>
/// T1's no-op tool: proves the stdio channel end-to-end (host → server → tool → response).
/// Replaced as the reference example once the 7 real tools land (T5–T8);
/// kept afterwards as a cheap connectivity probe.
/// </summary>
[McpServerToolType]
public static class PingTool
{
    [McpServerTool(Name = "ping")]
    [Description("Connectivity probe: returns 'pong' if the ReviewCheck MCP server is alive.")]
    public static string Ping() => "pong";
}
