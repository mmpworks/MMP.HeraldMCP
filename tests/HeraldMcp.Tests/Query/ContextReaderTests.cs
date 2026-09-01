// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Budgets;
using HeraldMcp.Core.Paths;
using HeraldMcp.Core.Query;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// herald_context (PRD section 5): the +/-N events around a target, keyed
/// by a stable id. Q3 settled the id as source + physical line, stable
/// under append (a later append never renumbers earlier lines). Every
/// returned event carries its own id so a reader can walk further.
/// </summary>
public sealed class ContextReaderTests : IDisposable
{
    private readonly string _root;

    public ContextReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "heraldmcp-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string WriteLog(string name, int count)
    {
        var path = Path.Combine(_root, name);
        var lines = Enumerable.Range(0, count).Select(i =>
            $$"""{"time":"2026-08-31T12:00:00.000+00:00","level":"INF","level_key":"information","level_rank":"2","category":"A","message":"event {{i}}"}""");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private (LogScanner scanner, ContextReader ctx, string id) Setup(int count = 20)
    {
        WriteLog("app.log", count);
        var resolver = new RootConfinedResolver(_root);
        var registry = new SourceRegistry(resolver, 50L << 20);
        var scanner = new LogScanner(registry, new ResultBudget(BudgetLimits.Default));
        var id = registry.List().First().Id;
        return (scanner, new ContextReader(registry), id);
    }

    [Fact]
    public void Search_events_carry_a_context_id()
    {
        var (scanner, _, sid) = Setup();
        var result = scanner.Search(sid, new EventFilter { SearchText = "event 5" }, take: 10);
        var eventId = result.Events[0]["id"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(eventId));
    }

    [Fact]
    public void Context_returns_the_window_around_a_target()
    {
        var (scanner, ctx, sid) = Setup();
        var target = scanner.Search(sid, new EventFilter { SearchText = "event 10" }, take: 10)
            .Events[0]["id"]!.GetValue<string>();
        var window = ctx.Context(target, before: 2, after: 2);
        Assert.Equal(5, window.Events.Count); // 10 plus/minus 2
        Assert.Contains(window.Events, e => e["message"]!.GetValue<string>() == "event 10");
        Assert.Contains(window.Events, e => e["message"]!.GetValue<string>() == "event 8");
        Assert.Contains(window.Events, e => e["message"]!.GetValue<string>() == "event 12");
    }

    [Fact]
    public void Context_clamps_at_the_start_of_file()
    {
        var (scanner, ctx, sid) = Setup();
        var target = scanner.Search(sid, new EventFilter { SearchText = "event 0" }, take: 10)
            .Events[0]["id"]!.GetValue<string>();
        var window = ctx.Context(target, before: 5, after: 2);
        Assert.Equal(3, window.Events.Count); // 0,1,2 — nothing before 0
    }

    [Fact]
    public void Context_id_is_stable_under_append()
    {
        var (scanner, ctx, sid) = Setup(count: 5);
        var target = scanner.Search(sid, new EventFilter { SearchText = "event 2" }, take: 10)
            .Events[0]["id"]!.GetValue<string>();
        // Append more lines; the earlier target must still resolve to event 2.
        File.AppendAllText(Path.Combine(_root, "app.log"),
            """{"time":"2026-08-31T12:00:00.000+00:00","level":"INF","level_key":"information","level_rank":"2","category":"A","message":"event 99"}""" + "\n");
        var window = ctx.Context(target, before: 0, after: 0);
        Assert.Single(window.Events);
        Assert.Equal("event 2", window.Events[0]["message"]!.GetValue<string>());
    }

    [Fact]
    public void Context_masks_by_default()
    {
        WriteLog("app.log", 1);
        File.WriteAllText(Path.Combine(_root, "app.log"),
            """{"time":"2026-08-31T12:00:00.000+00:00","level":"ERR","level_key":"error","level_rank":"4","category":"A","message":"key sk-proj4abcdefghijklmnopqrstu leaked"}""" + "\n");
        var resolver = new RootConfinedResolver(_root);
        var registry = new SourceRegistry(resolver, 50L << 20);
        var scanner = new LogScanner(registry, new ResultBudget(BudgetLimits.Default));
        var ctx = new ContextReader(registry);
        var sid = registry.List().First().Id;
        var target = scanner.Search(sid, new EventFilter(), take: 10).Events[0]["id"]!.GetValue<string>();
        var window = ctx.Context(target, before: 0, after: 0);
        Assert.DoesNotContain("sk-proj4abcdefghijklmnopqrstu", window.Events[0].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_context_id_is_refused()
    {
        var (_, ctx, _) = Setup();
        Assert.Throws<InvalidEventIdException>(() => ctx.Context("garbage", before: 1, after: 1));
    }

    [Fact]
    public void Context_for_a_pruned_source_is_refused()
    {
        var (scanner, ctx, sid) = Setup();
        var target = scanner.Search(sid, new EventFilter { SearchText = "event 1" }, take: 10)
            .Events[0]["id"]!.GetValue<string>();
        File.Delete(Path.Combine(_root, "app.log"));
        var ex = Record.Exception(() => ctx.Context(target, before: 1, after: 1));
        Assert.True(ex is StaleSourceException or UnknownSourceException,
            $"a pruned source must be refused, got {ex?.GetType().Name ?? "success"}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
