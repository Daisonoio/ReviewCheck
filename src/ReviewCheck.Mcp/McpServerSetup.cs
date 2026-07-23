using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp;

/// <summary>
/// Wires the MCP server into the host: stdio transport + tool registration.
/// Kept separate from Program.cs so the 7 real tools (T5–T8) plug in here
/// without touching the entry point.
/// </summary>
public static class McpServerSetup
{
    public static void Configure(HostApplicationBuilder builder)
    {
        // stdout is reserved for the MCP JSON-RPC channel: console logs go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        // The seam (docs/23 §0): swap StubProvider for the real pipeline in MVP-2/3 — nothing else changes.
        builder.Services.AddSingleton<IReviewProvider, StubProvider>();
        builder.Services.AddSingleton(_ => new SessionStore());
        builder.Services.AddSingleton<ReviewEngine>();

        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "reviewcheck", Version = "0.1.0" })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
    }
}
