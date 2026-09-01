# Q1 field-name matrix and Q3 stable event identity

Status: closed by execution, 2026-08-31. Evidence tier: real sink output plus
real LogFileSearcher runs, against Herald.OSS at merge commit 28362f2
(origin/main, the tree PR #6 landed in). Method and scratch program are named
in the last section.

## One-line answers

- Q1 level filter: levelKey matches NOTHING on real JSON output. The json_file
  sink emits level_key (snake_case); LogFileSearcher filters on levelKey
  (camelCase). The round-3 claim is CONFIRMED, and the cause is a case
  convention split, not a missing field.
- Q3 stable id: there is NO collision-free Herald field. A three-event burst at
  one fixed millisecond produced one distinct full-field signature for three
  events. The {file, byte-offset} fallback is stable under append (measured).

## Repos and sinks covered

- Herald.OSS (package id Herald.OSS), the only tree a consumer of this server
  pins. Two file sinks ship: json_file (NDJSON) and text_file (plain text).
  Both were emitted and searched.
- Herald.OSS also carries two CLEF formatters (CompactJsonFormatter,
  RenderedCompactJsonFormatter) in the Serilog-compat layer. They are wired
  ONLY to the console sink (Console(ITextFormatter, ...)). There is no
  File(ITextFormatter) overload; the compat File(path, ...) overload infers the
  format from the extension and forwards to the native json_file / text_file
  sinks. So CLEF is NOT a file format a Herald.OSS consumer produces through the
  shipped API. It reaches a file only if the operator redirects console output
  with a shell pipe. Treated as out of scope for the file matrix; noted as an
  edge.
- A compact NDJSON variant (t/l/c/mt/m/p) exists in a separate,
  non-public Herald sink module that the Herald.OSS package does not
  reference. A Herald.OSS consumer does not produce it. Out of scope for
  v1; revisit if the server is pointed at that sink's output.

## Emitted-field matrix

Fields each in-scope sink writes per event, by execution.

| Field (normalized) | json_file key | text_file position |
|---|---|---|
| timestamp | time (ISO-8601 O, with offset) | inside the leading bracket group |
| level display | level (TRC/DBG/INF/WRN/ERR/FTL) | LEVEL in the bracket group |
| level key | level_key (information/warning/error/fatal/verbose/debug) | ABSENT (derived from the display abbreviation) |
| level rank | level_rank (string integer) | rank in the bracket group |
| category | category | before the first colon-space |
| message template | message_template | ABSENT |
| rendered message | message (often EMPTY, see note) | after the first colon-space |
| properties | properties object: name maps to value plus capture_mode plus format | trailing name=value pairs, unstructured |
| exception | context.key object: type, message, stackTrace, inner | ABSENT |

Note on message: on the kernel fast path (the typed Information / Warning /
Fatal calls with span properties) the emitted message is the empty string; the
text lives only in message_template. The rendered message was populated only on
the chain path (the exception overload). So message is frequently empty in real
json_file output.

## Filter behavior matrix

LogFileSearcher.Search run with each supported filter. MATCHES = returned the
expected events; NOTHING = returned zero on data that contains a match.

json_file (Core JsonFormatter):

| Filter | Result | Reason |
|---|---|---|
| level = error / warning / fatal | NOTHING (0) | filter reads levelKey; sink emits level_key |
| category ~ Ui | MATCHES (1) | both use category |
| search ~ text in message | NOTHING (0) | message empty on the kernel path |
| search ~ template hole | NOTHING (0) | filter reads messageTemplate; sink emits message_template |
| propKey = UserId | MATCHES (1) | both use properties |
| propKey + propValue | MATCHES (1) | value read from the nested value field |
| from / to date | MATCHES (all) | both use time |

text_file (PlainTextFormatter, parsed by the searcher regex):

| Filter | Result | Reason |
|---|---|---|
| level = error / warning | MATCHES (1) | plain-text parser projects levelKey from the abbreviation |
| level = fatal | NOTHING (0) | parser maps CRT to fatal, but the sink emits FTL |
| category ~ Ui | MATCHES (1) | category group |
| search ~ text | MATCHES (1) | plain text renders the message inline |
| search ~ template hole | NOTHING (0) | plain text has no template |
| propKey / propValue | NOTHING (0) | plain text has no structured properties |
| from / to date | MATCHES (all) | time group |

Line-classification edge (round 4, CONFIRMED): the searcher routes a line by
line.StartsWith open-brace. A JSON line with leading whitespace fails that test,
falls to the plain-text branch, fails the regex, and is counted in SkippedLines.
A five-line planted file (one clean JSON, one leading-whitespace JSON, one
malformed JSON, one blank, one junk-plain) returned matched=1, totalLines=5,
SkippedLines=3. Blank lines are not counted. The behavior fails safe: a hidden
line is surfaced in SkippedLines, never silently dropped.

## Normalization table

Target model for herald_search / herald_error_clusters / herald_context, and
the source field per format.

| Normalized field | from json_file | from text_file |
|---|---|---|
| time | time | regex time group |
| level key | level_key | derive from display abbreviation (fix FTL) |
| level rank | level_rank (parse to int) | regex rank group |
| category | category | regex category group |
| message | message, else render from template plus properties | regex message group |
| template | message_template | not available |
| properties | properties name/value pairs | trailing name=value pairs (best effort) |
| exception | context.key type/message/stackTrace/inner | not available |

Where normalization must live: the failing filters (level, template search) are
decided inside LogFileSearcher.MatchesFilters, which is internal to the searcher.
The section-11.1 reader overload lets the adapter own the reader, but not the
per-line field interpretation. So the field-name mismatch cannot be fixed in the
MCP adapter without rewriting the JSON on each line before it reaches the
searcher. The correct fix is upstream in LogFileSearcher (list below). The MCP
adapter still owns the mapping from the returned JsonElement to the normalized
result model above, and the render-from-template fallback for the empty message.

## Level ordering answer

Herald levels are extensible, and the ordering for a level >= comparison comes
from the level registry, not from the level key. The canonical base order is
verbose, debug, information, warning, error, fatal, which the registry assigns
ranks 0 to 5. Measured ranks in level_rank: information 2, warning 3, error 4,
fatal 5 (verbose 0, debug 1 by the same order). A custom level is inserted into
that order (RegisterBefore / RegisterAfter) and receives a rank in sequence.
ILogLevelRegistry exposes GetRank and IsAtOrAbove for the comparison.

Today LogFileSearcher does an equality match on the level string, so level >= does
not exist. The >= filter must compare by rank: read level_rank from the event,
or resolve the event level through ILogLevelRegistry.IsAtOrAbove.

## Q3 verdict

- No collision-free Herald field. A burst of three events at a fixed
  2026-08-31T12:00:00.123Z, same level, category, template, and properties,
  produced one distinct signature across all emitted fields (time, level_key,
  category, message, message_template, properties) for three events. Two events
  in the same millisecond with identical fields are indistinguishable by content.
- {file, byte-offset} fallback is stable under append. Appending a second batch
  to the same json_file grew the file 1175 to 1415 bytes with the existing
  prefix byte-identical, and the offset recorded before the append resolved to
  the same line after it. The sinks are append-only: WriteLine appends, and a
  second pipeline opening the same path appended without rewriting the prefix.
- Roll and prune create or delete whole files; they do not rewrite in place
  (sink design over MMP.RollingFiles; the roll itself was not executed here,
  evidence tier: design). So a byte-offset is stable for the lifetime of a file.
  An id held across a prune points at a deleted file and is REFUSED per PRD
  section 10, never remapped.

Recommendation: adopt file-identity plus byte-offset as the herald_context id,
where file-identity is a stable per-file token (not the mutable path), and apply
the PRD section 10 refusal when the file is gone.

## Upstream Herald.OSS changes this forces

Evidence-only task; each item becomes its own PR later.

1. LogFileSearcher.MatchesFilters: read level_key (not levelKey) for the level
   filter, and message_template (not messageTemplate) for text search. Without
   this the level filter and template search return nothing on every json_file
   event. Highest priority: it blocks herald_search outright.
2. LogFileSearcher: add level >= ordering by rank (parse level_rank, or use
   ILogLevelRegistry.IsAtOrAbove), replacing the current equality match.
3. LogFileSearcher.ParsePlainTextLine: the abbreviation map has CRT for fatal
   but the text_file sink emits FTL; fatal lines fail the level filter.
   Reconcile the abbreviation set with the canonical display names. The category
   capture (word characters only) also fails on categories with dots or spaces.
4. Text search should cover message_template when message is empty, because
   kernel-path events emit an empty message. Alternatively render the message
   from template plus properties before the text match.
5. Cheaper total-count strategy than a full-file scan per page (Q2, restated):
   Search recomputes TotalMatched over the whole file on every call.

## Method and impediments

- Scratch program: a net10 console app referencing Herald.OSS.csproj. It
  configured json_file and text_file sinks through QuickLogBuilder, logged a
  known event set (four levels, distinct categories, structured properties, a
  nested exception), disposed to flush, then ran LogFileSearcher.Search with
  every filter and printed matched counts. A fixed-clock provider forced the Q3
  collision burst. Kept in the session scratchpad under q1/.
- Impediment: the local a local Herald.OSS checkout checkout on branch main (6550e6a) is
  three commits BEHIND origin/main (28362f2) and does not contain PR #6 (no
  TextReader overload, no SkippedLines). The task said to work from origin/main.
  Worked around with a detached worktree at 28362f2 (a detached worktree at the pinned commit) and
  re-ran all evidence there. Cost: about ten minutes and one full checkout.
  Someone should fast-forward the local main.
- Impediment: QuickLogBuilder.WithCustomLevel(key, name) alone does not register
  the level in the runtime registry; GetByKeyOrNull returned null and a
  custom-level log threw. A placement (level order, or register-before/after) is
  needed. Dropped the custom-level emission; the canonical levels already give
  the rank evidence. Worth a docs note upstream.
