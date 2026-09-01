// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using System.Text.Json.Nodes;
using HeraldMcp.Core.Budgets;
using HeraldMcp.Core.Reading;
using HeraldMcp.Core.Redaction;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Core.Query;

/// <summary>One page of a search: masked events, truncation, skip count, token.</summary>
public sealed record SearchResult(
    IReadOnlyList<JsonNode> Events,
    bool Truncated,
    int SkippedLines,
    string? ContinuationToken,
    string SourceId);

/// <summary>
/// The herald_search engine (PRD section 5). Integrates every security
/// primitive in one read path: an opaque id resolves to a confined handle
/// (section 7.3), the handle is read through the bounded reader (section
/// 7.8), each line is normalized (Q1), the filter runs on the normalized
/// model (so level and template filters work), the budget shapes the page
/// (section 7.5), and every returned event is masked by default (section
/// 7.4). Skipped lines — unparseable non-blank lines plus over-length
/// lines — are counted and surfaced, never dropped.
/// </summary>
public sealed class LogScanner
{
    private readonly ResultBudget _budget;

    public LogScanner(SourceRegistry sources, ResultBudget budget)
    {
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    /// <summary>The source registry, exposed so herald_sources shares one instance.</summary>
    public SourceRegistry Sources { get; }

    public SearchResult Search(
        string sourceId,
        EventFilter filter,
        int take,
        string? continuationToken = null,
        bool redact = true,
        int maxLineLength = BoundedLineReader.DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var queryKey = QueryKey(sourceId, filter);
        var skip = continuationToken is null
            ? 0
            : _budget.DecodeToken(continuationToken, queryKey).Skip;

        // Each matched event carries its absolute ordinal among ALL parseable
        // events in the file (assigned pre-filter), so herald_context can
        // resolve it back to the same position later.
        var matched = new List<(int Ordinal, HeraldEvent Event)>();
        var totalMatched = 0;
        var skippedLines = 0;
        var ordinal = 0;

        using (var handle = Sources.OpenById(sourceId))
        using (var stream = new FileStream(handle, FileAccess.Read))
        using (var text = new StreamReader(stream))
        {
            var reader = new BoundedLineReader(text, maxLineLength);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var evt = HeraldEventParser.ParseJsonLine(line)
                          ?? HeraldEventParser.ParseTextLine(line);
                if (evt is null) { skippedLines++; continue; }

                var thisOrdinal = ordinal++;
                if (!filter.Matches(evt)) continue;

                totalMatched++;
                if (totalMatched <= skip + take + 1)
                    matched.Add((thisOrdinal, evt));
            }
            skippedLines += reader.SkippedOverlongLines;
        }

        var window = matched.Skip(skip).Select(m => ToJsonElement(m.Event, redact, EventId.Encode(sourceId, m.Ordinal)));
        var page = _budget.Take(window, totalMatched, skip, queryKey);

        return new SearchResult(
            page.Events.Select(JsonNodeFrom).ToList(),
            page.Truncated,
            skippedLines,
            page.ContinuationToken,
            sourceId);
    }

    /// <summary>
    /// Reads every matching event from a source (no paging) for the tools
    /// that need the whole window — clustering and window-diff. The caller
    /// scopes the window with the filter's time range. Over-length lines are
    /// skipped as in Search.
    /// </summary>
    public IReadOnlyList<HeraldEvent> ReadEvents(
        string sourceId, EventFilter filter,
        int maxLineLength = BoundedLineReader.DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var events = new List<HeraldEvent>();
        using var handle = Sources.OpenById(sourceId);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var text = new StreamReader(stream);
        var reader = new BoundedLineReader(text, maxLineLength);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = HeraldEventParser.ParseJsonLine(line) ?? HeraldEventParser.ParseTextLine(line);
            if (evt is not null && filter.Matches(evt)) events.Add(evt);
        }
        return events;
    }

    /// <summary>Shapes one event into masked result JSON without an id (for exemplars).</summary>
    public static JsonNode ShapeEvent(HeraldEvent e, bool redact) =>
        JsonNodeFrom(ToJsonElement(e, redact, eventId: null));

    /// <summary>Shapes one normalized event into its result JSON, stamped with its id and masked by default.</summary>
    internal static JsonElement ToJsonElement(HeraldEvent e, bool redact, string? eventId)
    {
        var node = new JsonObject();
        if (eventId is not null) node["id"] = eventId;
        node["time"] = e.Time.ToString("O");
        node["level"] = e.LevelKey;
        node["rank"] = e.LevelRank;
        node["category"] = e.Category;
        node["message"] = e.RenderedMessage;
        if (e.Template is not null) node["template"] = e.Template;
        if (e.Properties.Count > 0)
        {
            var props = new JsonObject();
            foreach (var (k, v) in e.Properties) props[k] = v;
            node["properties"] = props;
        }
        if (e.Exception is not null) node["exception"] = ExceptionNode(e.Exception);

        using var doc = JsonDocument.Parse(node.ToJsonString());
        var element = doc.RootElement.Clone();
        return redact ? ElementFrom(RedactionMasker.MaskElement(element)) : element;
    }

    private static JsonObject ExceptionNode(HeraldException ex)
    {
        var node = new JsonObject
        {
            ["type"] = ex.Type,
            ["message"] = ex.Message,
        };
        if (ex.StackTrace is not null) node["stackTrace"] = ex.StackTrace;
        if (ex.Inner is not null) node["inner"] = ExceptionNode(ex.Inner);
        return node;
    }

    private static JsonElement ElementFrom(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonNode JsonNodeFrom(JsonElement element) =>
        JsonNode.Parse(element.GetRawText())!;

    // The query fingerprint the continuation token binds to (PRD A6): a
    // source plus filter that changes between pages invalidates the token.
    private static string QueryKey(string sourceId, EventFilter f) =>
        string.Join('|', new[]
        {
            sourceId, f.MinLevelRank?.ToString(), f.Category, f.SearchText,
            f.PropertyKey, f.PropertyValue, f.From?.ToString("O"), f.To?.ToString("O"),
        });
}
