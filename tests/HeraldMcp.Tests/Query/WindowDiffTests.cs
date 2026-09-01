// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Query;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// herald_window_diff (PRD section 5, KEPT per section 11.2, anchor A17).
/// Deterministic comparison of two windows: new kinds, gone-quiet kinds,
/// and rate deltas, with a stated formula, defined zero-baseline and
/// gone-quiet semantics, a fixed sort, and top-N.
/// </summary>
public sealed class WindowDiffTests
{
    private static HeraldEvent E(string type, string message = "m") => new()
    {
        Time = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
        LevelKey = "error",
        LevelRank = 4,
        Category = "App",
        Message = message,
        Exception = new HeraldException(type, message, null, null),
    };

    [Fact]
    public void A_kind_only_in_current_is_reported_as_new()
    {
        var baseline = new[] { E("TypeA") };
        var current = new[] { E("TypeA"), E("TypeB") };
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        Assert.Contains(diff.NewKinds, k => k.Signature.Contains("TypeB"));
        Assert.Equal(1, diff.NewKinds.Single(k => k.Signature.Contains("TypeB")).CurrentCount);
    }

    [Fact]
    public void A_kind_only_in_baseline_is_reported_as_gone_quiet()
    {
        var baseline = new[] { E("TypeA"), E("TypeGone") };
        var current = new[] { E("TypeA") };
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        Assert.Contains(diff.GoneQuiet, k => k.Signature.Contains("TypeGone"));
    }

    [Fact]
    public void Rate_delta_is_current_minus_baseline()
    {
        var baseline = Enumerable.Repeat(0, 2).Select(_ => E("T")).ToArray();
        var current = Enumerable.Repeat(0, 5).Select(_ => E("T")).ToArray();
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        var change = diff.Changed.Single();
        Assert.Equal(2, change.BaselineCount);
        Assert.Equal(5, change.CurrentCount);
        Assert.Equal(3, change.Delta);
    }

    [Fact]
    public void Zero_baseline_reports_new_without_dividing()
    {
        var baseline = Array.Empty<HeraldEvent>();
        var current = Enumerable.Repeat(0, 4).Select(_ => E("T")).ToArray();
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        var nk = diff.NewKinds.Single();
        Assert.Equal(0, nk.BaselineCount);
        Assert.Equal(4, nk.CurrentCount);
        Assert.Equal(4, nk.Delta);
    }

    [Fact]
    public void Changed_kinds_sort_by_absolute_delta_descending()
    {
        var baseline = new List<HeraldEvent>();
        baseline.AddRange(Enumerable.Repeat(0, 10).Select(_ => E("Shrink")));
        baseline.AddRange(Enumerable.Repeat(0, 1).Select(_ => E("Grow")));
        var current = new List<HeraldEvent>();
        current.AddRange(Enumerable.Repeat(0, 2).Select(_ => E("Shrink")));  // -8
        current.AddRange(Enumerable.Repeat(0, 6).Select(_ => E("Grow")));    // +5
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        Assert.Contains("Shrink", diff.Changed[0].Signature); // |−8| > |+5|
    }

    [Fact]
    public void Unchanged_kinds_are_not_reported_as_changed()
    {
        var baseline = new[] { E("Steady"), E("Steady") };
        var current = new[] { E("Steady"), E("Steady") };
        var diff = WindowDiff.Compare(baseline, current, topN: 10);
        Assert.Empty(diff.Changed);
        Assert.Empty(diff.NewKinds);
        Assert.Empty(diff.GoneQuiet);
    }

    [Fact]
    public void TopN_caps_each_section_independently()
    {
        var baseline = new List<HeraldEvent>();
        var current = new List<HeraldEvent>();
        for (var i = 0; i < 10; i++)
            current.AddRange(Enumerable.Repeat(0, i + 1).Select(_ => E($"New{i}")));
        var diff = WindowDiff.Compare(baseline, current, topN: 3);
        Assert.Equal(3, diff.NewKinds.Count);
        Assert.Equal(10, diff.NewKinds[0].CurrentCount); // largest first
    }

    [Fact]
    public void Deterministic_across_runs_with_equal_deltas()
    {
        var baseline = Array.Empty<HeraldEvent>();
        var current = new[] { E("Alpha"), E("Bravo") }; // both delta +1
        var a = WindowDiff.Compare(baseline, current, topN: 10);
        var b = WindowDiff.Compare(baseline, current, topN: 10);
        Assert.Equal(
            a.NewKinds.Select(k => k.Signature),
            b.NewKinds.Select(k => k.Signature)); // stable tie-break by signature
    }

    [Fact]
    public void Empty_windows_yield_empty_diff()
    {
        var diff = WindowDiff.Compare(Array.Empty<HeraldEvent>(), Array.Empty<HeraldEvent>(), topN: 10);
        Assert.Empty(diff.NewKinds);
        Assert.Empty(diff.GoneQuiet);
        Assert.Empty(diff.Changed);
    }
}
