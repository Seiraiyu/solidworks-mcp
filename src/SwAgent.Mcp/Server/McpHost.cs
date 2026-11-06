using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SwAgent.Client;
using SwAgent.Mcp.Logging;

namespace SwAgent.Mcp.Server;

/// <summary>
/// <c>swmcp serve</c>: an MCP server on stdio. Stdout carries only the protocol; logs go to stderr and to
/// <c>%LOCALAPPDATA%\solidworks-mcp\logs\server-&lt;yyyyMMdd&gt;.log</c>.
/// </summary>
public static class McpHost
{
    public const string ServerName = "solidworks-mcp";

    /// <summary>The environment variable that raises or lowers the server's minimum log level (default Information).</summary>
    public const string LogLevelVariable = "SWMCP_LOG_LEVEL";

    /// <summary>The server's identity and tools; the stdio host and the tests both configure through this.</summary>
    public static void Configure(McpServerOptions options, IEnumerable<McpServerTool> tools)
    {
        options.ServerInfo = new Implementation { Name = ServerName, Version = SwAgentClient.Version };
        options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools) options.ToolCollection.Add(tool);
    }

    /// <summary>The minimum level <c>SWMCP_LOG_LEVEL</c> names (trace, debug, information, warning, error, critical, none); Information when unset or unknown.</summary>
    public static LogLevel MinimumLevel(string? value) =>
        value is { Length: > 0 } && !char.IsDigit(value[0]) && Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;

    public static async Task<int> RunAsync(SupervisorOptions options, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.SetMinimumLevel(MinimumLevel(Environment.GetEnvironmentVariable(LogLevelVariable)));
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory, "server"));

        Supervisor? supervisor = null;
        builder.Services.AddMcpServer().WithStdioServerTransport();
        builder.Services.AddOptions<McpServerOptions>()
            .Configure<ILoggerFactory>((o, logs) =>
            {
                // The supervisor logs through the host's loggers, so it is made when the host configures the server.
                options.Client.Logger = logs.CreateLogger("SwAgent.Client");
                supervisor = new Supervisor(options);
                Configure(o, ToolCatalog.Create(supervisor, logs.CreateLogger("SwAgent.Mcp")));
            });

        try
        {
            using var host = builder.Build();
            var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SwAgent.Mcp");
            log.LogInformation("swmcp {Version} serving MCP on stdio; tools: {Tools}", SwAgentClient.Version, string.Join(", ", ToolCatalog.For(options.ApiCall).Select(t => t.Name)));
            if (!options.ApiCall) log.LogInformation("the escape hatch (sw_api_call) is off");
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        finally
        {
            // Closing the connection lets the add-in restore the user's preferences at once.
            supervisor?.Dispose();
        }
    }
}
