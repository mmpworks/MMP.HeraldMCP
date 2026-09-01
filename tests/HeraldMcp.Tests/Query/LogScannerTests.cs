// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using HeraldMcp.Core.Budgets;
using HeraldMcp.Core.Paths;
using HeraldMcp.Core.Query;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// End-to-end scan (PRD section 5 herald_search): source id -> confined
/// handle -> bounded reader -> parse -> filter -> budget -> masked result.
/// This is the integration of every security primitive; a planted secret
/// in a matching event must come back masked, an over-length line must be
/// counted not read, and results must page.
/// </summary>
public sealed class LogScannerTests : IDisposable
{
    private readonly string _root;

    public LogScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "heraldmcp-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string WriteLog(string name, params string[] lines)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private LogScanner Scanner(BudgetLimits? limits = null)
    {
        var resolver = new RootConfinedResolver(_root);
        var registry = new SourceRegistry(resolver, 50L * 1024 * 1024);
        return new LogScanner(registry, new ResultBudget(limits ?? BudgetLimits.Default));
    }

    private static string Json(string level, string levelKey, int rank, string category, string message) =>
        $$"""{"time":"2026-08-31T12:00:0{{rank}}.000+00:00","level":"{{level}}","level_key":"{{levelKey}}","level_rank":"{{rank}}","category":"{{category}}","message":"{{message}}"}""";

    private string IdFor(LogScanner scanner, string displayName) =>
        scanner.Sources.List().First(s => s.DisplayName == displayName).Id;

    [Fact]
    public void Search_returns_matching_events_from_a_json_source()
    {
        WriteLog("app.log",
            Json("INF", "information", 2, "Ui", "started"),
            Json("ERR", "error", 4, "Db", "query failed"),
            Json("WRN", "warning", 3, "Ui", "slow render"));
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"), new EventFilter { MinLevelRank = 3 }, take: 100);
        Assert.Equal(2, result.Events.Count);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void Level_filter_works_on_real_snake_case_json()
    {
        // The whole reason the adapter parses itself: this returns matches
        // where the raw LogFileSearcher returns nothing.
        WriteLog("app.log",
            Json("INF", "information", 2, "A", "a"),
            Json("ERR", "error", 4, "A", "b"));
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"),
            new EventFilter { MinLevelRank = 4 }, take: 100);
        Assert.Single(result.Events);
    }

    [Fact]
    public void Planted_secret_in_a_match_is_masked()
    {
        WriteLog("app.log",
            Json("ERR", "error", 4, "Auth", "login with Bearer abcDEF0123456789abcDEF0123456789"));
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"), new EventFilter(), take: 100);
        var payload = result.Events[0].ToJsonString();
        Assert.DoesNotContain("abcDEF0123456789abcDEF0123456789", payload, StringComparison.Ordinal);
        Assert.Contains("MASKED", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void No_redact_mode_shows_the_raw_value()
    {
        WriteLog("app.log",
            Json("ERR", "error", 4, "Auth", "steve@example.com signed in"));
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"), new EventFilter(), take: 100, redact: false);
        var payload = result.Events[0].ToJsonString();
        Assert.Contains("steve@example.com", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Unparseable_nonblank_lines_are_counted_not_dropped()
    {
        WriteLog("app.log",
            Json("INF", "information", 2, "A", "ok"),
            "this is not a herald line",
            "{malformed json",
            "");
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"), new EventFilter(), take: 100);
        Assert.Single(result.Events);
        Assert.Equal(2, result.SkippedLines); // blank line not counted
    }

    [Fact]
    public void Overlong_line_is_counted_and_never_read_into_memory()
    {
        var huge = new string('x', 2 * 1024 * 1024); // over the 1 MiB cap
        WriteLog("app.log",
            Json("INF", "information", 2, "A", "before"),
            huge,
            Json("INF", "information", 2, "A", "after"));
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "app.log"), new EventFilter(), take: 100);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void Results_page_with_a_continuation_token()
    {
        var lines = Enumerable.Range(0, 10)
            .Select(i => Json("INF", "information", 2, "A", $"m{i}")).ToArray();
        WriteLog("app.log", lines);
        var scanner = Scanner(new BudgetLimits(MaxEvents: 4, MaxSerializedBytes: 1 << 20, TokenTtl: TimeSpan.FromMinutes(5)));

        var id = IdFor(scanner, "app.log");
        var seen = 0;
        string? token = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = scanner.Search(id, new EventFilter(), take: 4, continuationToken: token);
            seen += page.Events.Count;
            if (!page.Truncated) break;
            token = page.ContinuationToken;
        }
        Assert.Equal(10, seen);
    }

    [Fact]
    public void Text_format_source_is_scanned()
    {
        WriteLog("plain.log",
            "[2026-08-31T12:00:00.000+00:00 ERR:4] Db: connection dropped UserId=7",
            "[2026-08-31T12:00:01.000+00:00 INF:2] Ui: ok");
        var scanner = Scanner();
        var result = scanner.Search(IdFor(scanner, "plain.log"),
            new EventFilter { MinLevelRank = 4 }, take: 100);
        Assert.Single(result.Events);
    }

    [Fact]
    public void Unknown_source_id_is_refused()
    {
        var scanner = Scanner();
        WriteLog("app.log", Json("INF", "information", 2, "A", "x"));
        scanner.Sources.List();
        Assert.Throws<UnknownSourceException>(
            () => scanner.Search("ffffffffffffffff", new EventFilter(), take: 10));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
