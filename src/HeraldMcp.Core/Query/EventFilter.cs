// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Query;

/// <summary>
/// The filter herald_search applies to normalized events (PRD section 5).
/// All set criteria combine with AND. The level filter compares by RANK
/// (Q1: extensible levels order by rank), so "warning and above" works
/// regardless of the level's string. Text search covers the rendered
/// message, the template, property values, and the exception type, so a
/// kernel-path event with an empty message still matches its template.
/// </summary>
public sealed record EventFilter
{
    /// <summary>Minimum level rank, inclusive. Null means no level filter.</summary>
    public int? MinLevelRank { get; init; }

    /// <summary>Case-insensitive substring on the category. Null means any.</summary>
    public string? Category { get; init; }

    /// <summary>Case-insensitive substring across message/template/props/exception.</summary>
    public string? SearchText { get; init; }

    public string? PropertyKey { get; init; }
    public string? PropertyValue { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    public bool Matches(HeraldEvent e)
    {
        if (MinLevelRank is { } min && e.LevelRank < min) return false;

        if (!string.IsNullOrEmpty(Category)
            && !e.Category.Contains(Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (From is { } from && e.Time < from) return false;
        if (To is { } to && e.Time > to) return false;

        if (!string.IsNullOrEmpty(PropertyKey))
        {
            if (!e.Properties.TryGetValue(PropertyKey, out var value)) return false;
            if (!string.IsNullOrEmpty(PropertyValue)
                && !string.Equals(value, PropertyValue, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrEmpty(SearchText) && !MatchesText(e, SearchText))
            return false;

        return true;
    }

    private static bool MatchesText(HeraldEvent e, string needle)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        if (e.RenderedMessage.Contains(needle, ci)) return true;
        if (e.Template is not null && e.Template.Contains(needle, ci)) return true;
        foreach (var v in e.Properties.Values)
            if (v.Contains(needle, ci)) return true;
        for (var ex = e.Exception; ex is not null; ex = ex.Inner)
        {
            if (ex.Type.Contains(needle, ci)) return true;
            if (ex.Message.Contains(needle, ci)) return true;
        }
        return false;
    }
}
