// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Query;

/// <summary>An exception captured on an event (Q1: context.*).</summary>
public sealed record HeraldException(string Type, string Message, string? StackTrace, HeraldException? Inner);

/// <summary>
/// One log event normalized to a single model regardless of source format
/// (PRD Q1 table). This is what herald_search, herald_error_clusters, and
/// herald_context operate on, so the level and template filters work even
/// though Herald's own searcher cannot match its own json_file output.
/// </summary>
public sealed record HeraldEvent
{
    public required DateTimeOffset Time { get; init; }
    public required string LevelKey { get; init; }
    public required int LevelRank { get; init; }
    public required string Category { get; init; }

    /// <summary>The emitted rendered message, possibly empty on the kernel path.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The message template, when the format carries one.</summary>
    public string? Template { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; }
        = new Dictionary<string, string>();

    public HeraldException? Exception { get; init; }

    /// <summary>
    /// The message a reader should see: the emitted message when present,
    /// otherwise the template with its holes filled from properties (Q1
    /// fallback for the kernel path, which emits an empty message).
    /// </summary>
    public string RenderedMessage =>
        !string.IsNullOrEmpty(Message) ? Message
        : Template is not null ? RenderTemplate(Template, Properties)
        : string.Empty;

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> props)
    {
        if (props.Count == 0 || !template.Contains('{')) return template;
        var sb = new System.Text.StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            var close = template.IndexOf('}', open);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, open - i);
            var name = template.Substring(open + 1, close - open - 1).TrimStart('@', '$');
            sb.Append(props.TryGetValue(name, out var v) ? v : template[open..(close + 1)]);
            i = close + 1;
        }
        return sb.ToString();
    }
}
