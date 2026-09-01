// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HeraldMcp.Tests.Server;

/// <summary>
/// The true end-to-end anchor (PRD A1): spawn the built server as a real
/// process and drive it with the MCP SDK client over stdio JSON-RPC. This
/// covers the one layer the in-process tests do not — the transport
/// framing — and proves a client can list the tools and get an answer.
/// Marked as an integration collection so it is easy to exclude on a
/// machine without the built server.
/// </summary>
[Trait("kind", "integration")]
public sealed class StdioTransportTests : IAsyncLifetime, IDisposable
{
    private string _root = string.Empty;
    private string _serverDll = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "heraldmcp-stdio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.log"),
            """{"time":"2026-08-31T12:00:01.000+00:00","level":"ERR","level_key":"error","level_rank":"4","category":"Auth","message":"tok Bearer abcDEF0123456789abcDEF0123456789"}""" + "\n");
        _serverDll = LocateServerDll();
        return Task.CompletedTask;
    }

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "herald-mcp",
            Command = "dotnet",
            Arguments = new[] { _serverDll, _root },
        });
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Client_lists_all_five_tools()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "herald_context", "herald_error_clusters", "herald_search", "herald_sources", "herald_window_diff" },
            names);
    }

    [Fact]
    public async Task Client_calls_herald_sources_then_search_and_gets_a_masked_answer()
    {
        await using var client = await ConnectAsync();

        var sources = await client.CallToolAsync("herald_sources",
            new Dictionary<string, object?>());
        var sourcesText = TextOf(sources);
        using var sdoc = JsonDocument.Parse(sourcesText);
        var id = sdoc.RootElement.GetProperty("sources")[0].GetProperty("id").GetString()!;

        var search = await client.CallToolAsync("herald_search",
            new Dictionary<string, object?> { ["sourceId"] = id, ["minLevel"] = "error" });
        var searchText = TextOf(search);
        using var rdoc = JsonDocument.Parse(searchText);
        Assert.Equal(1, rdoc.RootElement.GetProperty("events").GetArrayLength());
        // The transport carried the answer AND masking held across it.
        Assert.DoesNotContain("abcDEF0123456789abcDEF0123456789", searchText, StringComparison.Ordinal);
    }

    private static string TextOf(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().First();
        return block.Text;
    }

    private static string LocateServerDll()
    {
        // Walk up from the test output to the repo, then to the server build.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "HeraldMcp.Server", "bin");
            if (Directory.Exists(candidate))
            {
                var dll = Directory.EnumerateFiles(candidate, "HeraldMcp.Server.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (dll is not null) return dll;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException("Built HeraldMcp.Server.dll not found; build the solution first.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
