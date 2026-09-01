# Herald MCP

A read-only [MCP](https://modelcontextprotocol.io) server that lets an AI
agent search your [Herald](https://github.com/mmpworks/Herald.OSS) log
files. Point an MCP client at a log directory and ask what broke; the
agent gets structured search, error clustering, event context, and
before/after window comparison — over stdio, on your machine, with secrets
masked by default.

License: Apache 2.0.

## What it does

Five tools, all read-only:

| Tool | Answers |
|---|---|
| `herald_sources` | What can I query, and how fresh is it? |
| `herald_search` | Show me the errors from source X since 14:00 |
| `herald_error_clusters` | Group these exceptions so I see the patterns |
| `herald_context` | Show me what happened right around this event |
| `herald_window_diff` | What changed after the 14:02 deploy? |

It reads Herald's `json_file` and `text_file` output. It never writes to
your logs, opens no network connection, and runs only over stdio.

## Five-minute start

You need the .NET 10 SDK and an MCP client (Claude Code, Claude Desktop,
or Cursor).

1. Clone and build:
   ```
   git clone https://github.com/mmpworks/MMP.HeraldMCP
   cd MMP.HeraldMCP
   dotnet build -c Release
   ```
2. Point your client at it. For Claude Desktop, add to
   `claude_desktop_config.json`:
   ```json
   {
     "mcpServers": {
       "herald": {
         "command": "dotnet",
         "args": ["path/to/HeraldMcp.Server.dll", "C:\\logs\\myapp"]
       }
     }
   }
   ```
   The last argument (or several) is the log directory to serve. Give more
   than one to serve more than one root.
3. Ask your agent: "Use herald_sources to list my log files, then find the
   errors in the last hour." The agent calls the tools; you read the
   answer.

The server refuses to start if you give it no directory, or a directory
that does not exist. It prints one plain sentence to stderr and exits.

## What it will not do

- **Write anything to your logs.** Every tool is read-only by
  construction, verified by a byte-hash test over the corpus before and
  after a full run.
- **Open a network port.** v1 is stdio only. There is no HTTP server and
  no `--bind` flag, because localhost is a network boundary, not a trust
  boundary (`docs/security.md` explains why).
- **Hand your agent a cleartext secret by default.** A built-in masker
  hides key material, bearer tokens, connection strings, and common PII
  shapes in every result. Pass `redact: false` on a tool call to turn it
  off for that call. The masker is a heuristic with a real
  false-negative boundary — see `docs/security.md`.

## Scope and limits

- **Formats:** `json_file` (NDJSON) and `text_file`. Not gzip, protobuf, or
  CSV. Plain, non-rotating files.
- **Size:** a declared supported ceiling of 50 GiB per served directory.
  Over that, the server refuses with a plain sentence rather than degrade
  silently. There is no index in v1; a search is a full scan.
- **Herald dependency:** `Herald.OSS` >= 0.14.0 from NuGet (the release
  that carries the reader overload the server needs). Restored
  automatically on build.

## Where things are

| Path | What |
|---|---|
| `docs/tools.md` | The tool reference: every parameter, result field, and error |
| `docs/security.md` | The security model and its residual risks, stated in full |
| `docs/PRD.md` | The build contract and its version history |
| `docs/prd-design-meetings/` | How the design was hardened before any code |
| `src/HeraldMcp.Core` | The library: reading, parsing, redaction, path safety, budgets, query |
| `src/HeraldMcp.Server` | The stdio host and the five tools |
| `tests/HeraldMcp.Tests` | The test suite, including live-attack and fuzz cases |

## Building and testing

```
dotnet build -c Release
dotnet test
```

The test suite plants real symlinks, junctions, oversized lines, and
secret corpora, and runs seeded fuzz sweeps over the reader, masker,
parser, clusterer, and continuation tokens. One suite spawns the built
server and drives it over real stdio JSON-RPC.
