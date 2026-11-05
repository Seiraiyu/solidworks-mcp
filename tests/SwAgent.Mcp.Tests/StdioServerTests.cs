using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

/// <summary>The real swmcp.exe, started as an MCP client starts it: stdin and stdout carry the protocol.</summary>
public class StdioServerTests
{
    static string Swmcp => Path.Combine(AppContext.BaseDirectory, "swmcp.exe");

    /// <summary>The .NET this test runs on, for swmcp.exe's apphost (a per-user SDK is not on its search path).</summary>
    static string DotnetRoot => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));

    static string NewLogDirectory() => Path.Combine(Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Serve_speaks_MCP_on_stdio_and_logs_to_its_file()
    {
        string logs = NewLogDirectory();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = Swmcp,
            Arguments = new[] { "serve" },
            EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_ROOT"] = DotnetRoot, ["SWMCP_LOG_DIRECTORY"] = logs },
        });
        await using (var client = await McpClient.CreateAsync(transport))
        {
            Assert.Equal(McpHost.ServerName, client.ServerInfo!.Name);
            Assert.Equal("sw_status", Assert.Single(await client.ListToolsAsync()).Name);
        }
        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(logs, "server-*.log")));
        Assert.Contains("serving MCP on stdio; tools: sw_status", log);
    }

    [Fact]
    public async Task Serve_exits_when_its_client_closes_stdin()
    {
        var start = new ProcessStartInfo(Swmcp, "serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_ROOT"] = DotnetRoot;
        start.Environment["SWMCP_LOG_DIRECTORY"] = NewLogDirectory();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stdout);
        Assert.Contains("serving MCP on stdio", await stderr);
    }
}
