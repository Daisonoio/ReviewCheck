using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReviewCheck.Llm;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;
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

        // The seam (docs/23 §0): the real pipeline is the default since MVP-2.
        // REVIEWCHECK_PROVIDER=stub keeps the fixture provider (demos, tests without a repo).
        if (string.Equals(Environment.GetEnvironmentVariable("REVIEWCHECK_PROVIDER"), "stub",
                StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IReviewProvider, StubProvider>();
        }
        else
        {
            // Repo root = where the host launched the server (the project dir via .mcp.json);
            // REVIEWCHECK_REPO overrides it explicitly.
            var repoRoot = Environment.GetEnvironmentVariable("REVIEWCHECK_REPO")
                           ?? Directory.GetCurrentDirectory();
            builder.Services.AddSingleton<IDiffReader>(_ => new LocalDiffReader(repoRoot));
            builder.Services.AddSingleton<AnalysisPipeline>();

            // Narrative seam (docs/25 §11): LLM narration when the user configured a key,
            // deterministic facts otherwise. REVIEWCHECK_NARRATOR=facts forces the floor
            // even with a key present (demos, offline, cost control).
            var factsForced = string.Equals(Environment.GetEnvironmentVariable("REVIEWCHECK_NARRATOR"), "facts",
                StringComparison.OrdinalIgnoreCase);
            if (!factsForced && AnthropicByoProvider.IsConfigured)
            {
                builder.Services.AddSingleton<ILlmProvider>(_ =>
                    new AnthropicByoProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
                builder.Services.AddSingleton<IBlockNarrator, LlmAdapter>();
            }
            else
            {
                builder.Services.AddSingleton<IBlockNarrator, FactsNarrator>();
            }

            builder.Services.AddSingleton<IReviewProvider, PipelineProvider>();
        }

        builder.Services.AddSingleton(_ => new SessionStore());
        builder.Services.AddSingleton<ReviewEngine>();

        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "reviewcheck", Version = "0.1.0" })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
    }
}
