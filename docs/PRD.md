# PRD — MMP.HeraldMCP

> **BUILD STATUS (2026-08-31): v1 implemented and green.** All five tools,
> all eight security anchors, and the four Core primitives are built
> red-first with fuzz coverage; 159 tests pass, including a live stdio
> round-trip against the built server (A1). Q1/Q3/Q4 are closed
> (`docs/q1-field-matrix.md`); Q5 closed in round 3. Q2 (upstream
> LogFileSearcher fixes) is filed as follow-up. Remaining before public
> release: the Herald.OSS release cut containing 28362f2 (§8 version pin),
> the opensource-sanitizer pass (A10), and the maintainer's go for public repo
> creation. See `docs/build-notes.md` for decisions and impediments,
> including the reuse-scope change Q1 forced.

Status: v0.6. Every review blocker is closed: four round-3 items were
editorial (fixed in v0.4), and the fifth (read interposition — B1) was
DECIDED by the maintainer on 2026-08-31: **fork (a) — a Search overload that
accepts a caller-provided reader**, delivered as
`mmpworks/Herald.OSS` PR #6 — MERGED 2026-08-31 (28362f2). Note: the change
landed in Herald.OSS, not Herald.Core — during implementation the architect
proved the B4 naming split is a real source drift (the consumer pins
Herald.OSS; a standalone Herald.Core tree cannot build), so Herald.OSS is
the only target that reaches the consumer and is testable. Three review rounds, three
FAILs, and no reviewer ever attacked the architecture — every failure was
one class, a claim more confident than its mechanism. Mapping:
`docs/redteam/CONSOLIDATED-round3.md`; process record:
`docs/prd-design-meetings/`. PR #6 is merged; §11.2 decided KEEP; round 4 closed
with the reviewers in agreement (the red-team reviewer PASS on the fixed text). **BUILD
SIGN-OFF: given by the maintainer, 2026-08-31.** The build phase starts with the
Q1-Q4 deliverables; the §8 release gate (a Herald.OSS release containing
28362f2) remains open and is recorded here when the release is cut.

License: Apache 2.0. Stage: 3+ (publishable OSS).

## 0. What this is, and what the rounds changed

Round 1 found v0.1 rebuilt a log reader Herald already ships. The reuse
that survives is `Herald.OSS`'s `LogFileSearcher` (query by field/text/date
with pagination) — and round 3 confirmed all wrapped types are public
across the shipped package boundary (§8, Q5 CLOSED).

Rounds 2–3 corrected three overclaims (all verified against Herald source):
Herald's `RedactionProcessor` and `ConfinedPathResolver` do NOT do what
early drafts said (emit-time / lexical-only), so the read-boundary masker
and the path-safety check are **new security-critical code here**, not
wraps. And the query reuse itself is narrower than "wrap the reader":
because `LogFileSearcher.Search` takes a path and does its own I/O, the
adapter could not bound or handle-protect the read; §11.1 resolved this
with a Herald.OSS reader overload (fork a, PR #6). So the true scope is: **reuse
the query FILTER + result shape; write redaction, path-safety, bounded
reading, clustering, and normalization here.** Not a thin adapter — a
focused server over Herald's filter.

The three forced security decisions stand: stdio-only, redaction ON by
default, opaque source IDs.

## 1. What

A read-only MCP server that exposes Herald log queries to AI agents over
stdio. A developer points an MCP client (Claude Code, Claude Desktop,
Cursor) at their Herald logs and asks what broke. The server uses
`LogFileSearcher`'s filter for the query and adds its own read-boundary
redaction, path-safety, bounded reading, and clustering. It is not a sink,
not a forwarder, and never writes to the log stream.

## 2. Why

- Observability's next consumer is an agent. The loop "agent reads the
  failing test → reads the logs → proposes the fix" needs a structured,
  safe read surface; today that is grep or a per-project Loki API.
- For Herald: "the logging stack built for the agent era," over the query
  engine Herald already ships.
- For the house: our agents debug Herald-instrumented apps through it.

## 3. Users

1. A dev running an MCP client against their own dev/staging Herald logs
   (primary; community).
2. Our cast, debugging Herald and Herald-instrumented apps.
3. (Later, paid) teams pointing agents at shared/remote logs — needs auth
   and remote backends explicitly OUT of v1.

## 4. Prior art and sizing

- Closest prior art by shape: `grafana/mcp-grafana` and `grafana/loki-mcp`
  — BOTH confirmed to exist (Q4 closed 2026-08-31 by live fetch).
  `loki-mcp` is official Grafana, Go, ~166 stars, actively maintained, and
  REQUIRES a running Loki instance — which confirms our differentiator:
  local-first, no running backend needed.
- In-house prior art: `Herald.OSS`'s `MMP.Herald.Addons.Query.LogFileSearcher`.
  v1 reuses its filter. Differentiator vs grafana is local-first (no running
  backend) plus semantic tools.
- Sizing: at the stated inputs, ~0.86 GB/day (10 ev/s × 1 KB) to ~86 GB/day
  (100 ev/s × 10 KB). **Declared supported ceiling for v1: 50 GB per served
  source directory** (a few days of a busy app, inside the band). Corpus
  size is detected by summing file sizes in the served root at discovery;
  over the ceiling, `herald_sources` and every query refuse with one plain
  sentence, never silent degradation. The no-index decision holds only
  under that ceiling, proven by A16.
- Paging-cost interaction (from source): `LogFileSearcher.Search` computes
  the total match count by scanning the whole file on every call, so
  following a continuation token to the end is O(pages × file size). §4,
  A6, and A16 all state this; a cheaper count strategy is a Q2 deliverable.
- Gem check (INDEX.md, 2026-08-31): `plain-sentence-error-surface`
  (tool-result + ceiling errors), `decide-at-authoritative-boundary`
  (path + redaction decided at the read boundary — the read boundary has
  LESS context than emit, which is why the masker is content-pattern code).

## 5. Tool surface (v1) — five tools

Read-only. Every result carries `truncated: bool`, `skipped_lines: int`
(non-blank lines the scan could not parse into an event — an attacker can hide a line by
malforming it, so the count is surfaced, never swallowed; delivered via
the §11.1(a) reader overload), `source: {id}`, and untrusted log
content in a dedicated `content` field. Every error is one plain sentence +
remedy. **Tools take an opaque source ID, never a filesystem path.**

| Tool | Question | Backing |
|---|---|---|
| `herald_sources` | "What can I query, and how fresh is it?" | discovery + metadata; opaque ids, time spans, event counts (a full scan at large corpus — stated), schema fields, last-write freshness |
| `herald_search` | "Errors from source X since 14:00" | LogFileSearcher filter. TWO known gaps to close before build (Q1/Q2): its `level` filter is literal-match, not `>=`; and it keys on `levelKey`, which — strongly indicated from source — NO Herald JSON sink emits, so the level filter may match nothing on real output until the field-name matrix is run and normalization is added |
| `herald_error_clusters` | "Group the exceptions in this window" | new clustering over search results: exception type + normalized message + top frame; top-N with counts, first/last seen, one exemplar |
| `herald_window_diff` | "What changed after 14:02?" | two windows compared: new cluster kinds, rate deltas, gone-quiet sources — KEPT by §11.2 decision, pinned by A17 |
| `herald_context` | "Everything around this event" | ±N events by STABLE event id. Primary id from a Herald field if Q3 finds a collision-free one; FALLBACK id is `{file, byte-offset}` (Q3 must confirm offset STABILITY under in-place rewrite, not just field existence) |

Dropped in v0.2, stays dropped: `herald_health` — its drop counters live
in Herald's in-process sinks (`IPipelineDropSink` → Prometheus
`herald_sink_drops_total`), not the files this server reads.

Also NOT in v1: streaming/subscribe, writes, alerts, dashboards, remote
backends, network transport.

## 6. Editions

Rule: every OSS seam ships with at least one working OSS implementation, or
it is a rotting speculative interface.

- **Community (this repo, Apache 2.0):** all five tools over local files;
  baseline redaction ON by default; a minimal local audit line (timestamp,
  tool, result count, and filter fields WITH SECRET-SHAPED VALUES MASKED —
  filters are agent-supplied strings that can themselves be content like
  `text=Bearer eyJ...`); opaque source ids; the interfaces the paid lanes
  plug into, each with a working OSS default. The audit artifact is written
  OUTSIDE the served roots.
- **Paid (not here):** remote/Loki backends, SSO/RBAC, tenant isolation,
  managed redaction policy packs, durable audit export, fleet management.
  NOT redaction-at-all and NOT basic auditability — those ship free.

## 7. Security model — eight points

Logs are a data-exfiltration surface, and this hands an agent (and,
through injection, an attacker influencing it) a search API over whatever
the app wrote.

1. **stdio only in v1. No network listener exists.** HTTP and
   `--unsafe-bind` are struck: localhost is a network boundary, not a trust
   boundary (Docker DNAT, WSL2 loopback bridging, editor port forwarding,
   DNS rebinding), none of it gated by a process flag. HTTP returns only in
   a later version with Origin validation and auth as prerequisites.
2. **Read-only to the corpus.** No write or delete access to served corpus
   objects. Files opened with `FILE_SHARE_READ | FILE_SHARE_WRITE |
   FILE_SHARE_DELETE` — share-write lets Herald keep appending, share-delete
   lets Herald prune, and neither grants US write, so "changes nothing"
   holds while the reader never stalls the pipeline it reads (round 3: omit
   share-write and a mid-scan writer is blocked; omit share-delete and the
   prune is blocked). Concurrent append is thus allowed mid-scan, so a
   torn-tail / partial last line is expected and handled (A2). The community
   audit line is the ONE thing the process writes, outside the served roots.
   Any index lives outside the served roots.
3. **Path containment — NEW adapter code, not reuse.** Herald's
   `ConfinedPathResolver` is a lexical prefix check only. The live attack it
   misses: a symlink planted INSIDE a served root resolves lexically inside
   the root, passes a prefix compare, and the open follows it out to
   `~/.aws/credentials` — opaque IDs do NOT stop this, discovery walks the
   root. So the server resolves the FINAL path from the OPENED HANDLE and
   re-checks against roots, refusing the Windows escape family (junction,
   UNC, 8.3 alias, alternate data stream, TOCTOU swap). DISCOVERY is
   inside the same perimeter: enumeration does not traverse reparse points
   (directory symlinks/junctions), and no file's metadata is surfaced by
   `herald_sources` or summed into the §4 ceiling unless it passes the
   same root check — otherwise a planted directory symlink is a metadata
   oracle over out-of-root files with nothing ever opened. This is real work,
   not a flag: `GetFinalPathNameByHandle` returns a `\\?\` (sometimes
   volume-GUID) path that must be normalized before the root compare; a
   directory handle needs `FILE_FLAG_BACKUP_SEMANTICS`; .NET has no managed
   `O_NOFOLLOW`, so the Unix side is a P/Invoke; and the validated handle
   must be the handle that is READ — which is the §11.1 decision. Pinned by
   A14.
4. **Redaction ON by default — NEW adapter code, not reuse.** Herald's
   `RedactionProcessor` is emit-time, needs a render context absent at read
   time, matches property NAMES not content shapes, and its helpers are
   `internal`. The masker is new pattern-based code in `HeraldMcp.Core` over
   the `JsonElement` the reader returns: it masks key material, bearer
   tokens, connection strings, and common PII shapes in any field.
   `--no-redact` restores raw parity. Masking runs BEFORE truncation.
   Residual in full: a heuristic content masker has a false-negative
   boundary (a novel secret shape passes) and a false-positive cost (it may
   mask a benign look-alike); we reduce disclosure, we do not guarantee its
   absence. This placement (post-Search, on JsonElements) needs no read
   interposition and is unaffected by §11.1. Pinned by A12.
5. **Result budgets.** Hard caps: ≤1000 events AND ≤1 MiB serialized bytes
   per result; ≤5 s scan-time and ≤4 concurrent calls; a query-bound
   continuation token that expires after 5 minutes (all configurable
   defaults). `truncated: true` on any cut, never silent. The budget bounds
   each CALL; it does not prevent whole-log extraction across paged calls,
   and following the token to the end costs O(pages × file size) (§4).
6. **Injection: three levers, all used.** Width = budgets (5). Content =
   default masking (4). Framing = untrusted content in a dedicated result
   FIELD, tool descriptions state log content is data, raw exemplar text
   behind an explicit opt-in. Residual in full: (a) a determined poisoned
   line can still reach a client that ignores the framing — we cannot fix
   clients; (b) the ROOT-CONFIG channel is a trust boundary — roots arrive
   from the MCP client's launch config, and a repo-level `.mcp.json` could
   point the server at `~/.ssh`. The server cannot verify which config file
   chose its roots, so this is an OPERATOR EXPECTATION, stated loudly in the
   README: configure roots from user-level config only; opaque IDs are not
   authorization; the invoking account and parent client are trusted to
   select roots. This is the C1 argument in stdio clothing (exposure
   outside the process).
7. **No telemetry. The server contacts nothing at runtime.** Verified by an
   egress-monitored test (A11). The only network the test tolerates is the
   one-time `dotnet restore`/NuGet fetch at build, which it names and
   excludes; runtime egress is zero.
8. **Parse hardening at the ADAPTER boundary.** The wrapped
   `LogFileSearcher` does NOT guard input: it iterates `File.ReadLines` with
   no length cap, so one newline-free line is an OOM writable by anyone who
   can write a log line. v1 enforces a ≤1 MiB line cap and encoding check
   with a bounded reader — an over-length line is DISCARDED THROUGH ITS
   NEXT NEWLINE and counts as ONE skipped line, so its tail can never
   re-enter as spurious lines — ≤64 JSON depth (`JsonDocument` default), and
   linear-time normalization with no unbounded regex. The bounded reader protects the search because
   §11.1(a) gives Search a caller-provided reader overload — Search no
   longer re-opens the file itself. Pinned by A15.

Five owner questions (external-conduct, Stage 3+): (1) runs as the invoking
user, no stored creds; (2) reads only user-configured roots, no corpus
writes, index + audit outside roots; (3) instructions only from the stdio
client — no network listener exists; (4) spawns nothing, contacts nothing;
(5) stderr diagnostics + the community audit line.

## 8. Technical shape

- .NET 10, C#. Official MCP C# SDK `ModelContextProtocol` (Core package;
  NO `.AspNetCore` in v1 — stdio only). Version pinned at planning.
- Layout: `src/HeraldMcp.Core` (LogFileSearcher filter adapter, schema
  normalization, NEW read-boundary masker, NEW handle-based path check, NEW
  bounded reader, clustering — no transport), `src/HeraldMcp.Server` (stdio
  host), `tests/`.
- **Dependency: `Herald.OSS` >= 0.14.0 (NuGet), net10.0.** 0.14.0 is the
  release that carries the reader overload (merge 28362f2) plus
  SkippedLines. Restored from nuget.org on build; no local checkout needed.
  Round 3 confirmed against the shipped `Herald.OSS.0.13.0.nupkg` that all
  wrapped types are public across the package boundary:
  `MMP.Herald.Addons.Query.LogFileSearcher` + `LogFileSearchResult`,
  `MMP.Herald.Output.Writers.ConfinedPathResolver` + `ILogFilePathResolver`
  + `LineSanitizer`, `MMP.Herald.Output.Rendering.RedactionProcessor` +
  `RedactionRule`. (Q5 CLOSED; pin the minimum so a later drop cannot
  narrow the surface.) NOTE: "Herald.Core" names a source project inside the
  Herald solution, NOT the package a consumer references — the package is
  `Herald.OSS`.
- v1 reuses only the query FILTER + result shape; it does NOT wrap
  `RedactionProcessor` (emit-time) or `ConfinedPathResolver` (lexical). v1
  reads NDJSON schemas with a normalization table (Q1); text/protobuf/csv
  are out-of-scope, stated in the README.
- AOT-friendly; no reflection JSON. The five-minute bar is A1.

## 9. Anchor — assertions written before code

An assertion without a threshold is a confirmation, not an anchor. Free
design constants carry their number here; hardware-dependent performance
numbers are set at the A16 benchmark and named as such (accurate, not a
dodge).

Functional: A1 five-minute clone-to-answer (timed; named prereqs excluded);
A2 known-answer search over a planted corpus INCLUDING a malformed line
(must appear in `skipped_lines`, not vanish) and a torn-tail partial last
line (must not crash); A3 three exception families → three clusters + one
singleton; A16 benchmark — at the DECLARED 50 GB ceiling on named hardware,
cold-start / p95 query / peak memory each ≤ a threshold set AT the benchmark
and recorded, the ceiling+1 corpus returns the plain-sentence refusal, and
a full-continuation paging run records its O(pages × size) cost; A17 (if
window_diff ships) known-answer window comparison with fixed windows, a
stated rate-delta formula, defined zero-baseline and gone-quiet semantics,
sort, top-N, truncation.

Result-budget: A6 — a result is cut at ≤1000 events AND ≤1 MiB serialized
bytes; a query exceeding ≤5 s scan-time or the ≤4 concurrency limit is
refused; the continuation token is query-bound and expires at 5 minutes;
following it reconstructs the full count; the O(pages × size) cost is
recorded, not hidden.

Security (each §7 point → assertion):
- **A5** (7.2) read-only: corpus byte-hash identical before/after the full
  suite; handles are `FILE_SHARE_READ|WRITE|DELETE`; ONLY the external audit
  artifact changes; index outside roots.
- **A11** (7.7) no runtime outbound network: suite under egress monitoring,
  zero runtime connections; the one-time build-time restore is named and
  excluded.
- **A12** (7.4) redaction: a planted-secret adversarial corpus (a named,
  enumerated set of key-material and PII patterns) is masked in EVERY tool's
  output AND in audit-line filter values by default; masking runs before
  truncation; `--no-redact` shows it; the false-negative boundary is
  documented, not asserted away.
- **A13** (7.1) transport: default and only transport is stdio; no listener
  binds any port during a full session.
- **A14** (7.3) path safety: `../`, a symlink INSIDE a root pointing out, a
  junction, a UNC path, an 8.3 alias, and an alternate data stream are each
  REFUSED; a DIRECTORY symlink inside a root pointing out is not traversed
  by discovery — the out-of-root subtree appears in neither
  `herald_sources` nor the ceiling sum; a TOCTOU swap between validate and
  open is caught. VALID only
  once §11.1 makes the read use the validated handle.
- **A15** (7.8) parse hardening: an oversized (newline-free) line, a deeply
  nested JSON value, and a ReDoS-shaped input each complete within ≤256 MiB
  and ≤10 s; the ≤1 MiB line cap holds against the ACTUAL read path — the
  bounded reader IS the read path via the §11.1(a) Herald.OSS overload.
- **A18** (7.6) config trust boundary: repo/project-level root config is
  ignored or refused; only user-level configured roots are honored; an
  opaque ID cannot address outside the configured set. (Added round 3 — the
  §7.6 rule was previously unanchored.)
- **A8** (7.6) injection framing: a planted instruction-shaped line is
  returned in the untrusted `content` field, byte-faithful under
  `--no-redact`, never interpreted; a default run masks any secret-shaped
  substring in it.
- **A9** license/provenance: LICENSE byte-identical to canonical Apache
  2.0; SPDX headers; a script enumerates TRANSITIVE dependency licenses and
  flags incompatible/missing metadata.
- **A10** the public repo passes the opensource-sanitizer pass.
- **A19** (§10) concurrent-mutation contract: known-answer cases for a
  source renamed, replaced, truncated, deleted, appended, and pruned
  during discovery, search, context lookup, and a continuation page — each
  yields the defined gap indication or stale-token error, never a silent
  partial. (Added round 4 — §10 was the strongest unpinned contract left.)

Dispositions of removed v0.1 anchors (so no deletion is silent): A4
SUPERSEDED by A14 (traversal + Windows escape family + handle check). A7
was a v0.1 live-append assertion; its content folds into A2's torn-tail
case and A5's live-handle sharing — recorded, not dropped.

Coverage (§7 point → assertion): 1→A13, 2→A5, 3→A14, 4→A12, 5→A6,
6→A8+A12+A18, 7→A11, 8→A15. All eight pinned. A14 and A15 became
satisfiable when §11.1 decided fork (a).

## 10. Non-goals (v1)

No writes/alerts/dashboards; no network transport or HTTP; no auth system;
no remote/Loki backend (paid seam); no text/protobuf/csv formats; no gzip.
Plain files only. Tolerated concurrent-mutation behavior IS defined for
every operation (discovery, search, context lookup, and each continuation
page): on a source renamed, replaced, truncated, or deleted the tool
returns an explicit gap indication (never a silent partial); a continuation
token binds to a file identity + length snapshot and returns a deterministic
stale/gap error when that snapshot cannot be maintained; an opaque ID held
across a prune is REFUSED with a plain sentence, never silently remapped.
No `herald_health`. No index (measured follow-on). No plain-version loss —
a human who wants to read logs opens the files.

## 11. Open decisions for the maintainer

1. **Read interposition (B1) — DECIDED: fork (a), the maintainer, 2026-08-31.
   MERGED as `mmpworks/Herald.OSS` PR #6 (merge commit 28362f2,
   2026-08-31).**
   `LogFileSearcher.Search` took a path and opened the file itself, so the
   adapter could not bound the read (§7.8) or guarantee the validated
   handle was the read handle (§7.3). Resolution: a `Search(TextReader, …)`
   overload — TextReader, not Stream, because the scan is line-based and
   the caller must own encoding, buffering, and line-length bounding; a
   Stream would pull unbounded line-splitting back inside the searcher.
   The path overload delegates (byte-identical, existing tests
   unmodified); the searcher never disposes the caller's reader and has no
   filesystem fallback (proven by a test that uses no file at all). The
   adapter opens once, validates the handle (§7.3), wraps it in the
   bounded reader (§7.8), passes it in — A14 and A15 are satisfiable.
   PR #6 is merged; the §8 minimum Herald.OSS version rises to the first
   release cut from main at or after merge commit 28362f2 (recorded when
   the release cascade completes). The PR's second commit
   also DELIVERS `SkippedLines` on the result record as a non-positional
   init-only property — verified source- and binary-compatible (contra
   the round-3 assumption that it required a breaking change). Semantics:
   non-blank lines the scan could not parse into an event; blank lines
   and filtered-out valid events are not counted. Round-4 re-review runs
   now that the mechanism exists.
2. **`herald_window_diff` — DECIDED: KEEP (the maintainer, 2026-08-31).** Ships in
   v1, pinned by A17 with deterministic semantics.
3. **Public repo creation timing** — gated on your explicit go; not done.
4. **Registry name** (`herald-mcp`?) at publish.

## 12. Open questions (must close before build sign-off)

Q1–Q3 determine whether three of five tools can meet their contracts.

1. Run the FIELD-NAME MATRIX: emit a file from each Herald JSON sink, run
   LogFileSearcher against it with every filter, record which field names
   each sink actually writes. Strong indication from source: no sink emits
   `levelKey`, so the level filter may match nothing today — this makes the
   normalization a REQUIRED change, not "may force." Include the
   line-classification edge found in round 4: the merged searcher routes a
   line by `StartsWith('{')`, so valid JSON with leading whitespace lands
   in SkippedLines (fails safe — surfaced, never silent). Deliver the
   definitive NDJSON schema list + normalization table + where it is
   hosted. the architect.
2. Deliverables around LogFileSearcher: the bounded reader wired per §11.1,
   `level >=` ordering vs today's literal-match, a `skipped_lines` count,
   and a cheaper total-count strategy than full-file-scan-per-page.
3. `herald_context` stable event identity: a collision-free Herald field,
   else confirm the `{file, byte-offset}` fallback AND its stability under
   in-place rewrite / rotation.
4. CLOSED (2026-08-31, live fetch): `grafana/loki-mcp` exists — official
   Grafana org, Go MCP server over Loki, ~166 stars, active, requires a
   running Loki instance. The v0.1 citation was correct; safe to compare
   publicly.
5. CLOSED (round 3): `Herald.OSS` exposes all wrapped types public across
   the shipped net10.0 / 0.13.0 boundary. 0.13.0 is the TYPE-VISIBILITY
   floor only; the BUILD floor is the first release containing `28362f2`
   (§8) — 0.13.0 lacks the reader overload and SkippedLines, verified
   against its shipped binary.
