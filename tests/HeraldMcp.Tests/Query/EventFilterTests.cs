// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Query;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// The filter that herald_search applies to normalized events (PRD section
/// 5). The level filter is a rank >= comparison (Q1: extensible levels
/// order by rank, not string), which the raw searcher cannot do. Text
/// search covers the rendered message AND the template, so kernel-path
/// events with an empty message still match.
/// </summary>
public sealed class EventFilterTests
{
    private static HeraldEvent Ev(
        string level = "information", int rank = 2, string category = "App",
        string message = "hello world", string? template = null,
        (string, string)[]? props = null, DateTimeOffset? time = null) =>
        new()
        {
            Time = time ?? new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
            LevelKey = level,
            LevelRank = rank,
            Category = category,
            Message = message,
            Template = template,
            Properties = (props ?? Array.Empty<(string, string)>())
                .ToDictionary(p => p.Item1, p => p.Item2),
        };

    [Fact]
    public void Empty_filter_matches_everything()
    {
        var f = new EventFilter();
        Assert.True(f.Matches(Ev()));
    }

    [Fact]
    public void Level_filter_is_a_rank_at_or_above_comparison()
    {
        var f = new EventFilter { MinLevelRank = 3 }; // warning and above
        Assert.False(f.Matches(Ev(level: "information", rank: 2)));
        Assert.True(f.Matches(Ev(level: "warning", rank: 3)));
        Assert.True(f.Matches(Ev(level: "error", rank: 4)));
    }

    [Fact]
    public void Category_filter_is_case_insensitive_substring()
    {
        var f = new EventFilter { Category = "ui" };
        Assert.True(f.Matches(Ev(category: "Ui.Button")));
        Assert.False(f.Matches(Ev(category: "Database")));
    }

    [Fact]
    public void Text_search_matches_rendered_message()
    {
        var f = new EventFilter { SearchText = "world" };
        Assert.True(f.Matches(Ev(message: "hello world")));
    }

    [Fact]
    public void Text_search_matches_template_when_message_is_empty()
    {
        // The kernel-path case the raw searcher misses.
        var f = new EventFilter { SearchText = "clicked" };
        Assert.True(f.Matches(Ev(message: "", template: "clicked {Id}",
            props: new[] { ("Id", "42") })));
    }

    [Fact]
    public void Text_search_matches_rendered_template_values()
    {
        var f = new EventFilter { SearchText = "42" };
        Assert.True(f.Matches(Ev(message: "", template: "clicked {Id}",
            props: new[] { ("Id", "42") })));
    }

    [Fact]
    public void Property_key_filter_matches_presence()
    {
        var f = new EventFilter { PropertyKey = "UserId" };
        Assert.True(f.Matches(Ev(props: new[] { ("UserId", "7") })));
        Assert.False(f.Matches(Ev(props: new[] { ("Other", "1") })));
    }

    [Fact]
    public void Property_key_and_value_both_must_match()
    {
        var f = new EventFilter { PropertyKey = "UserId", PropertyValue = "7" };
        Assert.True(f.Matches(Ev(props: new[] { ("UserId", "7") })));
        Assert.False(f.Matches(Ev(props: new[] { ("UserId", "8") })));
    }

    [Fact]
    public void Date_range_is_inclusive()
    {
        var t = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var f = new EventFilter { From = t, To = t };
        Assert.True(f.Matches(Ev(time: t)));
        Assert.False(f.Matches(Ev(time: t.AddSeconds(1))));
        Assert.False(f.Matches(Ev(time: t.AddSeconds(-1))));
    }

    [Fact]
    public void Filters_combine_with_and()
    {
        var f = new EventFilter { MinLevelRank = 4, Category = "db" };
        Assert.True(f.Matches(Ev(level: "error", rank: 4, category: "Db")));
        Assert.False(f.Matches(Ev(level: "error", rank: 4, category: "Ui")));
        Assert.False(f.Matches(Ev(level: "info", rank: 2, category: "Db")));
    }

    [Fact]
    public void Text_search_is_case_insensitive()
    {
        var f = new EventFilter { SearchText = "ERROR" };
        Assert.True(f.Matches(Ev(message: "an error occurred")));
    }

    [Fact]
    public void Exception_type_is_searchable()
    {
        var e = Ev(message: "boom") with
        {
            Exception = new HeraldException("System.TimeoutException", "slow", null, null),
        };
        var f = new EventFilter { SearchText = "TimeoutException" };
        Assert.True(f.Matches(e));
    }
}
