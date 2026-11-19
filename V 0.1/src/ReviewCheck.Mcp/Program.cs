using Microsoft.Extensions.Hosting;
using ReviewCheck.Mcp;

// T1 (docs/23 §6): MCP host over stdio. stdout carries the JSON-RPC protocol,
// so all logging must go to stderr — McpServerSetup takes care of that.
var builder = Host.CreateApplicationBuilder(args);
McpServerSetup.Configure(builder);
await builder.Build().RunAsync();
