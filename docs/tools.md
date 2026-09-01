# Tool reference

Five read-only tools. Every tool takes an opaque source id (from
`herald_sources`), never a file path. Every result is JSON. Log content is
untrusted data: it rides in dedicated fields and is masked by default. On a
handled error a tool returns `{"error": "<one plain sentence>"}` instead of
failing the call.

## herald_sources

Lists the queryable log files. Call it first; the ids it returns feed every
other tool.

Parameters: none.

Result:
```json
{
  "sources": [
    { "id": "a1b2c3d4e5f60718", "name": "app.log",
      "size_bytes": 20480, "last_write_utc": "2026-08-31T21:00:00.0000000+00:00" }
  ]
}
```

The `id` is a per-process token, not a path. It does not survive a server
restart, and a file that was pruned since the last `herald_sources` call is
refused (not silently remapped) when a later tool uses its id. If the
served directory is larger than the supported ceiling, this tool returns an
`error` naming the ceiling.

## herald_search

Searches one source's events.

| Parameter | Meaning |
|---|---|
| `sourceId` | Opaque id from `herald_sources`. Required. |
| `minLevel` | Minimum level, inclusive: `verbose`, `debug`, `information`, `warning`, `error`, `fatal`. Compared by rank, so `warning` matches warning and above. |
| `category` | Case-insensitive substring on the category. |
| `search` | Free text; matches the message, the template, property values, or the exception type. |
| `propertyKey` / `propertyValue` | Require a property by key, and optionally its value. |
| `from` / `to` | Inclusive UTC bounds, ISO-8601. |
| `take` | Max events this page (default 200). |
| `continuationToken` | The token from a prior page. |
| `redact` | `false` returns raw content. Default `true`. |

Result:
```json
{
  "source": "a1b2c3d4e5f60718",
  "events": [
    { "id": "<event id>", "time": "...", "level": "error", "rank": 4,
      "category": "Db", "message": "...", "properties": { }, "exception": { } }
  ],
  "truncated": true,
  "skipped_lines": 0,
  "continuation_token": "<token or null>"
}
```

Notes:
- `truncated` is true when more events matched than fit this page. Follow
  `continuation_token` to get the rest; following it to the end
  reconstructs the full match count. The token is bound to this specific
  query and expires after five minutes.
- `skipped_lines` counts non-blank lines the scan could not parse into an
  event, plus any line longer than the 1 MiB cap. A malformed line is
  surfaced here, never silently dropped.
- Each event's `id` is what `herald_context` takes.
- The level filter works on Herald's real `json_file` output. Herald's own
  file searcher does not, because of a field-name mismatch this server
  works around; see `docs/PRD.md` Q1.

## herald_error_clusters

Groups error and warning events into clusters by exception type, top stack
frame, and normalized message (numbers, GUIDs, and long hex runs are
collapsed so variants group together).

| Parameter | Meaning |
|---|---|
| `sourceId` | Required. |
| `minLevel` | Minimum level (default `warning`). |
| `from` / `to` | Inclusive UTC bounds. |
| `topN` | Number of clusters (default 20). |
| `redact` | Default `true`. |

Result: `{ "source": "...", "clusters": [ { "count": N, "first_seen_utc":
"...", "last_seen_utc": "...", "exemplar": { } } ] }`, ordered by count
descending. `count` reflects every matching event; `topN` caps only how
many clusters come back. Distinct non-numeric identifiers (two different
device names, say) form distinct clusters — the normalizer cannot tell a
device name from a word.

## herald_context

Returns the events around one event, by its `id` from a search result.

| Parameter | Meaning |
|---|---|
| `eventId` | An `id` from a `herald_search` result. Required. |
| `before` / `after` | How many events on each side (default 5 each). |
| `redact` | Default `true`. |

Result: `{ "source": "...", "target_ordinal": N, "events": [ ... ] }`. The
window clamps at the start and end of the file. The id is stable under
append: a later write to the file does not change what an earlier id points
at. An id for a pruned source is refused.

## herald_window_diff

Compares two time windows of one source and reports what changed.

| Parameter | Meaning |
|---|---|
| `sourceId` | Required. |
| `baselineFrom` / `baselineTo` | The earlier window, UTC ISO-8601. |
| `currentFrom` / `currentTo` | The later window, UTC ISO-8601. |
| `minLevel` | Minimum level (default `warning`). |
| `topN` | Number of kinds per section (default 20). |

Result:
```json
{
  "source": "...",
  "new_kinds":  [ { "signature": "...", "baseline_count": 0, "current_count": 5, "delta": 5 } ],
  "gone_quiet": [ { "signature": "...", "baseline_count": 4, "current_count": 0, "delta": -4 } ],
  "changed":    [ { "signature": "...", "baseline_count": 2, "current_count": 9, "delta": 7 } ]
}
```

`new_kinds` are error kinds present in the current window and absent in the
baseline (reported as a count, so a zero baseline needs no division).
`gone_quiet` is the reverse. `changed` is a kind in both whose count moved;
`delta` is current minus baseline. Each section sorts by magnitude and is
deterministic across runs.
