// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using HeraldMcp.Server;

namespace HeraldMcp.Tests.Server;

/// <summary>
/// End-to-end over the actual tool surface (the methods the MCP host
/// exposes), against a real on-disk corpus. This exercises the whole stack
/// — confinement, bounded reading, parsing, filtering, clustering, budget,
/// and default masking — through the same entry points a client calls. The
/// only layer not covered here is the SDK's JSON-RPC framing, which is SDK
/// code, not ours.
/// </summary>
public sealed class HeraldToolsIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly HeraldTools _tools;
    private readonly HeraldService _service;

    public HeraldToolsIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "heraldmcp-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.log"), string.Join('\n', new[]
        {
            J("2026-08-31T12:00:00.000+00:00", "INF", "information", 2, "Ui", "app started"),
            J("2026-08-31T12:00:01.000+00:00", "ERR", "error", 4, "Auth", "login failed for steve@example.com token Bearer abcDEF0123456789abcDEF0123456789"),
            J("2026-08-31T12:00:02.000+00:00", "ERR", "error", 4, "Db", "timeout after 30s"),
            J("2026-08-31T12:00:03.000+00:00", "ERR", "error", 4, "Db", "timeout after 45s"),
            J("2026-08-31T12:00:04.000+00:00", "WRN", "warning", 3, "Ui", "slow render 900ms"),
        }) + "\n");
        _service = new HeraldService(new HeraldServerOptions { Roots = new[] { _root } });
        _tools = new HeraldTools(_service);
    }

    private static string J(string time, string level, string levelKey, int rank, string cat, string msg) =>
        $$"""{"time":"{{time}}","level":"{{level}}","level_key":"{{levelKey}}","level_rank":"{{rank}}","category":"{{cat}}","message":"{{msg}}"}""";

    private string SourceId()
    {
        using var doc = JsonDocument.Parse(_tools.Sources());
        return doc.RootElement.GetProperty("sources")[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public void Sources_lists_the_corpus_with_an_opaque_id()
    {
        using var doc = JsonDocument.Parse(_tools.Sources());
        var sources = doc.RootElement.GetProperty("sources");
        Assert.Equal(1, sources.GetArrayLength());
        var id = sources[0].GetProperty("id").GetString()!;
        Assert.DoesNotContain("app.log", id);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, id);
    }

    [Fact]
    public void Search_finds_errors_and_masks_the_secret()
    {
        var json = _tools.Search(SourceId(), minLevel: "error");
        using var doc = JsonDocument.Parse(json);
        var events = doc.RootElement.GetProperty("events");
        Assert.Equal(3, events.GetArrayLength()); // two Db timeouts + one Auth error
        Assert.DoesNotContain("abcDEF0123456789abcDEF0123456789", json, StringComparison.Ordinal);
        Assert.DoesNotContain("steve@example.com", json, StringComparison.Ordinal);
        Assert.Contains("MASKED", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_no_redact_reveals_the_raw_value()
    {
        var json = _tools.Search(SourceId(), minLevel: "error", redact: false);
        Assert.Contains("steve@example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_level_filter_excludes_lower_levels()
    {
        var json = _tools.Search(SourceId(), minLevel: "warning");
        using var doc = JsonDocument.Parse(json);
        // 3 errors + 1 warning = 4; the INF line is excluded.
        Assert.Equal(4, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Search_category_and_text_filters_narrow_results()
    {
        var json = _tools.Search(SourceId(), category: "Db", search: "timeout");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Error_clusters_group_the_timeouts()
    {
        var json = _tools.ErrorClusters(SourceId(), minLevel: "error");
        using var doc = JsonDocument.Parse(json);
        var clusters = doc.RootElement.GetProperty("clusters");
        // The two "timeout after Ns" errors normalize to one cluster.
        Assert.Contains(clusters.EnumerateArray(), c => c.GetProperty("count").GetInt32() == 2);
    }

    [Fact]
    public void Context_returns_neighbours_of_an_event()
    {
        var searchJson = _tools.Search(SourceId(), search: "slow render");
        using var searchDoc = JsonDocument.Parse(searchJson);
        var eventId = searchDoc.RootElement.GetProperty("events")[0].GetProperty("id").GetString()!;

        var ctxJson = _tools.Context(eventId, before: 1, after: 1);
        using var ctxDoc = JsonDocument.Parse(ctxJson);
        Assert.True(ctxDoc.RootElement.GetProperty("events").GetArrayLength() >= 2);
    }

    [Fact]
    public void Window_diff_reports_a_new_kind_between_windows()
    {
        var id = SourceId();
        // Baseline: the first two seconds (no Db timeouts). Current: the window
        // that contains them.
        var json = _tools.WindowDiff(id,
            baselineFrom: "2026-08-31T12:00:00.000Z", baselineTo: "2026-08-31T12:00:00.500Z",
            currentFrom: "2026-08-31T12:00:02.000Z", currentTo: "2026-08-31T12:00:03.500Z",
            minLevel: "error");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("new_kinds").GetArrayLength() >= 1);
    }

    [Fact]
    public void Unknown_source_id_returns_a_plain_error_not_a_crash()
    {
        var json = _tools.Search("ffffffffffffffff", minLevel: "error");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.False(string.IsNullOrWhiteSpace(err.GetString()));
    }

    [Fact]
    public void Malformed_event_id_returns_a_plain_error()
    {
        var json = _tools.Context("not-a-real-id", before: 1, after: 1);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Every_tool_returns_valid_json()
    {
        var id = SourceId();
        foreach (var json in new[]
        {
            _tools.Sources(),
            _tools.Search(id),
            _tools.ErrorClusters(id),
            _tools.WindowDiff(id, "2026-08-31T12:00:00Z", "2026-08-31T12:00:01Z",
                "2026-08-31T12:00:02Z", "2026-08-31T12:00:05Z"),
        })
        {
            var ex = Record.Exception(() => JsonDocument.Parse(json).Dispose());
            Assert.Null(ex);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
