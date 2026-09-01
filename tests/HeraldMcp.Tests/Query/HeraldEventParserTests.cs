// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Query;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// The normalizer (PRD Q1 table). Fixtures reproduce the exact json_file
/// and text_file schemas verified against real Herald.OSS output
/// at merge commit 28362f2: json_file uses snake_case (time, level_key,
/// level_rank, category, message, message_template, properties,
/// context.*); text_file is "[time LEVEL:rank] Category: message name=val".
/// The parser reads BOTH into one model so the level and template filters
/// work — which the raw searcher cannot do (camelCase mismatch).
/// </summary>
public sealed class HeraldEventParserTests
{
    // Exact json_file shape from the Q1 matrix (snake_case keys).
    private const string JsonKernelPath =
        """{"time":"2026-08-31T12:00:00.100+00:00","level":"WRN","level_key":"warning","level_rank":"3","category":"Ui.Button","message":"","message_template":"clicked {Id}","properties":{"Id":{"value":"42","capture_mode":"scalar","format":null}}}""";

    private const string JsonExceptionPath =
        """{"time":"2026-08-31T12:00:01.000+00:00","level":"ERR","level_key":"error","level_rank":"4","category":"Db","message":"query failed","message_template":"query failed","context":{"ex":{"type":"System.TimeoutException","message":"timed out","stackTrace":"at Db.Run()","inner":null}}}""";

    private const string TextLine =
        "[2026-08-31T12:00:02.000+00:00 ERR:4] Db: connection dropped UserId=7";

    [Fact]
    public void Json_kernel_path_normalizes_all_fields()
    {
        var e = HeraldEventParser.ParseJsonLine(JsonKernelPath)!;
        Assert.Equal("warning", e.LevelKey);
        Assert.Equal(3, e.LevelRank);
        Assert.Equal("Ui.Button", e.Category);
        Assert.Equal("clicked {Id}", e.Template);
        Assert.Equal("42", e.Properties["Id"]);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 12, 0, 0, 100, TimeSpan.Zero), e.Time);
    }

    [Fact]
    public void Empty_message_is_rendered_from_template_and_properties()
    {
        // Kernel path emits message="" — the adapter renders it (Q1 fallback).
        var e = HeraldEventParser.ParseJsonLine(JsonKernelPath)!;
        Assert.Equal("clicked 42", e.RenderedMessage);
    }

    [Fact]
    public void Json_exception_path_captures_exception_fields()
    {
        var e = HeraldEventParser.ParseJsonLine(JsonExceptionPath)!;
        Assert.NotNull(e.Exception);
        Assert.Equal("System.TimeoutException", e.Exception!.Type);
        Assert.Equal("timed out", e.Exception.Message);
        Assert.Equal("query failed", e.RenderedMessage);
    }

    [Fact]
    public void Leading_whitespace_json_is_parsed_not_skipped()
    {
        // Improvement over the raw searcher, which routes leading-whitespace
        // JSON to the text parser and drops it into SkippedLines.
        var e = HeraldEventParser.ParseJsonLine("   " + JsonKernelPath);
        Assert.NotNull(e);
        Assert.Equal("warning", e!.LevelKey);
    }

    [Fact]
    public void Malformed_json_returns_null()
    {
        Assert.Null(HeraldEventParser.ParseJsonLine("{not valid json"));
    }

    [Fact]
    public void Text_line_normalizes_via_regex()
    {
        var e = HeraldEventParser.ParseTextLine(TextLine)!;
        Assert.Equal("error", e.LevelKey);
        Assert.Equal(4, e.LevelRank);
        Assert.Equal("Db", e.Category);
        Assert.Contains("connection dropped", e.RenderedMessage);
        Assert.Equal("7", e.Properties["UserId"]);
    }

    [Fact]
    public void Text_line_maps_FTL_to_fatal()
    {
        // Q1 upstream bug #3: the searcher maps CRT->fatal but the sink
        // emits FTL. The adapter maps FTL correctly.
        var e = HeraldEventParser.ParseTextLine("[2026-08-31T12:00:03.000+00:00 FTL:5] Boot: crash")!;
        Assert.Equal("fatal", e.LevelKey);
        Assert.Equal(5, e.LevelRank);
    }

    [Fact]
    public void Text_category_with_dots_is_captured()
    {
        // Q1 upstream bug #3: the searcher's category capture is word-only.
        var e = HeraldEventParser.ParseTextLine("[2026-08-31T12:00:04.000+00:00 INF:2] My.Long.Category: hi")!;
        Assert.Equal("My.Long.Category", e.Category);
    }

    [Fact]
    public void Junk_text_returns_null()
    {
        Assert.Null(HeraldEventParser.ParseTextLine("this is not a herald line"));
    }

    [Fact]
    public void Blank_line_returns_null_on_both_parsers()
    {
        Assert.Null(HeraldEventParser.ParseJsonLine(""));
        Assert.Null(HeraldEventParser.ParseTextLine("   "));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(2027)]
    [InlineData(88888)]
    public void Fuzz_parser_never_throws_on_hostile_lines(int seed)
    {
        var rng = new Random(seed);
        var chars = "{}[]\":,. \tabcERRWRN0123456789+-TZ_keylvl".ToCharArray();
        for (var n = 0; n < 1000; n++)
        {
            var len = rng.Next(0, 300);
            var line = string.Create(len, (rng, chars), static (span, s) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = s.chars[s.rng.Next(s.chars.Length)];
            });
            // Neither parser may throw on any input; both may return null.
            var ex = Record.Exception(() =>
            {
                _ = HeraldEventParser.ParseJsonLine(line);
                _ = HeraldEventParser.ParseTextLine(line);
            });
            Assert.Null(ex);
        }
    }
}
