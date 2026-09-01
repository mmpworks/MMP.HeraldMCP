// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json.Nodes;
using HeraldMcp.Core.Reading;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Core.Query;

/// <summary>The window of events around a target (PRD section 5 herald_context).</summary>
public sealed record ContextResult(IReadOnlyList<JsonNode> Events, string SourceId, int TargetOrdinal);

/// <summary>
/// Returns the +/-N events around a target event id (PRD section 5, Q3).
/// The id decodes to a source and an ordinal; the source is re-opened
/// through the registry (so confinement and the stale/pruned refusal still
/// apply), re-scanned, and the events whose ordinal falls in
/// [ordinal-before, ordinal+after] are returned, each stamped with its own
/// id and masked by default. Ordinals are stable under append (Q3).
/// </summary>
public sealed class ContextReader
{
    private readonly SourceRegistry _sources;

    public ContextReader(SourceRegistry sources) =>
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));

    public ContextResult Context(
        string eventId, int before, int after, bool redact = true,
        int maxLineLength = BoundedLineReader.DefaultMaxLineLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(before);
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        var (sourceId, target) = EventId.Decode(eventId);
        var lo = Math.Max(0, target - before);
        var hi = target + after;

        var events = new List<JsonNode>(before + after + 1);
        var ordinal = 0;

        using var handle = _sources.OpenById(sourceId); // throws UnknownSource/Stale on a pruned source
        using var stream = new FileStream(handle, FileAccess.Read);
        using var text = new StreamReader(stream);
        var reader = new BoundedLineReader(text, maxLineLength);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = HeraldEventParser.ParseJsonLine(line)
                      ?? HeraldEventParser.ParseTextLine(line);
            if (evt is null) continue;

            var thisOrdinal = ordinal++;
            if (thisOrdinal < lo) continue;
            if (thisOrdinal > hi) break;

            var element = LogScanner.ToJsonElement(evt, redact, EventId.Encode(sourceId, thisOrdinal));
            events.Add(JsonNode.Parse(element.GetRawText())!);
        }

        return new ContextResult(events, sourceId, target);
    }
}
