# Security model

This server hands an AI agent a search API over whatever your application
wrote to its logs. Logs hold secrets and PII, and an agent that reads a log
line can be influenced by its content. This page states what the server
does about that, and — with equal prominence — what it does not guarantee.

The model has eight points. Each is backed by a test; the test name in
parentheses is the anchor.

## 1. Stdio only. No network listener exists.

v1 speaks MCP over stdio and nothing else. There is no HTTP server and no
bind flag. This is deliberate: localhost is a network boundary, not a trust
boundary. Docker publishes container ports past the host firewall, WSL2
bridges loopback between Linux and Windows, and editors forward ports —
sometimes publicly. A local bind is reachable from more places than
"local" suggests, and a bind flag cannot gate exposure that happens outside
the process. An HTTP transport, if it ever ships, will require origin
validation and authentication as prerequisites, not flags. (A13: no socket
binds during a full session.)

## 2. Read-only to your logs.

The server holds no write or delete handle to any served file. Files open
with read + write + delete SHARING, which lets Herald keep appending and
pruning while the server reads; it does not grant the server write access.
The one thing the process writes is an optional local audit line, and that
goes to a location outside the served directories. (A5: the corpus
byte-hash is identical before and after a full run; only the external audit
artifact changes.)

## 3. Path containment, from the opened handle.

Tools take an opaque id, not a path, so a caller cannot ask for an
arbitrary file. Discovery still walks the served directory, so the real
risk is a symlink or junction planted INSIDE a served root that points out
— for example a link that resolves to `~/.aws/credentials`. A lexical path
check (the kind Herald's own resolver does) passes such a link, because the
link's own path is inside the root.

So the server resolves the final path FROM THE OPENED HANDLE
(`GetFinalPathNameByHandle` on Windows, the `/proc/self/fd` link on Unix)
and re-checks it against the roots. The handle it validated is the handle
it reads, which also closes the race between a check and the open.
Discovery does not follow reparse points, and no file's metadata is
surfaced or summed into the size ceiling unless it passes the same check.
(A14: real `../`, file symlink, directory symlink, junction, UNC, 8.3
alias, and alternate-data-stream escapes are each planted and refused; the
out-of-root subtree appears in neither the source list nor the size sum.)

## 4. Redaction on by default — and its limits.

A masker runs on every event before it leaves the server, hiding key
material, bearer tokens, JWTs, connection-string credentials, cloud and
vendor tokens (AWS, GitHub, Slack, OpenAI-style), emails, SSNs, and
Luhn-valid payment-card numbers. Pass `redact: false` on a call to see raw
content for that call. Masking runs before truncation, so a secret cannot
survive by sitting past the byte cap.

This is new code in this server, not a reuse of Herald's redaction —
Herald's redactor runs at log-write time and matches property names, which
does not fit a read-time content scan.

**The residual, stated in full.** The masker is a heuristic over content.
It has a false-negative boundary: a secret in a shape it does not
recognize — a novel token format, a secret that looks like ordinary text —
passes through. It also has a false-positive cost: a benign string that
fits a pattern (a Luhn-valid number that is not a card) gets masked. The
masker reduces disclosure; it does not guarantee the absence of secrets. Do
not point this server at logs whose exposure you cannot tolerate on the
strength of a heuristic. (A12: a planted-secret corpus is masked in every
tool's output by default; the boundary is documented, not asserted away.)

## 5. Result budgets.

A result is cut at 1000 events or 1 MiB of serialized JSON, whichever comes
first, and `truncated` says so. A query that would scan too long or run too
concurrently is refused. The continuation token is signed, bound to its
query, and expires after five minutes. The budget bounds each call; it does
not stop an agent from paging the whole log across many calls, and at the
50 GiB ceiling following every page is expensive, because the current
searcher recomputes the total count on each page. (A6.)

## 6. Injection: the log is data, not instructions.

An attacker who can get a string into your logs — through any field your
app logs from user input — can try to plant instructions for the agent
that later reads them. The server uses the three levers it controls: it
bounds result width (5), masks content (4), and frames log content as data
in dedicated result fields with tool descriptions that say so. 

**The residual, stated in full.** Framing is not enforcement. A determined
poisoned line can still reach a client that ignores the framing, and this
server cannot fix the client. Treat tool output as untrusted data in
whatever consumes it. (A8: a planted instruction-shaped line is returned
byte-faithful in the untrusted content field, never interpreted, and its
secret-shaped substrings are masked.)

## 7. No telemetry.

The server contacts nothing at run time. (A11: the suite runs under egress
monitoring and sees zero runtime connections; the one-time package restore
at build is named and excluded.)

## 8. Parse hardening.

The server parses attacker-writable input, so it bounds it: a line over
1 MiB is discarded through its next newline and counted as one skipped
line, never read whole into memory; JSON depth is bounded; normalization is
a single linear pass with no backtracking regex. (A15: an oversized line, a
deeply nested value, and a ReDoS-shaped input each complete within a fixed
memory and time ceiling.)

## The configuration trust boundary

One thing the server cannot enforce: which configuration chose its roots.
The roots arrive as launch arguments from the MCP client. A project-level
client config (a checked-in `.mcp.json`, say) could point the server at a
sensitive directory. The server cannot tell which config file supplied its
arguments, so this is an operator expectation, not an assertion:

- Configure roots from your USER-level client config, not from a config
  file that travels with a repository.
- An opaque id is not an authorization token; it only names a file already
  inside a configured root.
- The account that launches the server, and the client that launches it,
  are trusted to choose the roots.

## What to check before pointing it at production logs

1. Are the logs' secrets tolerable to expose on a heuristic masker (point
   4)? If not, do not serve them, or serve a redacted copy.
2. Are the roots set from user-level config, not a repo file (the
   configuration boundary)?
3. Is the consumer treating tool output as untrusted data (point 6)?
