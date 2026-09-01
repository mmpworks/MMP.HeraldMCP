// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// herald-mcp: a read-only MCP server over Herald log files, stdio only.
// Usage: herald-mcp <log-root> [<log-root> ...]
// Roots come from the launch command (the trusted parent client); the
// server honors only what it is given and never widens them (PRD 7.6).

var roots = args.Where(a => !a.StartsWith('-')).ToArray();
if (roots.Length == 0)
{
    Console.Error.WriteLine(
        "herald-mcp: give at least one log directory to serve, e.g. herald-mcp C:\\logs\\myapp");
    return 1;
}

foreach (var root in roots)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"herald-mcp: log root not found: {root}");
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// stdio is the protocol channel, so logs must go to stderr, never stdout.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new HeraldServerOptions { Roots = roots });
builder.Services.AddSingleton<HeraldService>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
