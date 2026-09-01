// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Query;

/// <summary>One kind's change between two windows.</summary>
public sealed record KindChange
{
    public required string Signature { get; init; }
    public required int BaselineCount { get; init; }
    public required int CurrentCount { get; init; }

    /// <summary>The rate-delta formula (A17): current minus baseline.</summary>
    public int Delta => CurrentCount - BaselineCount;
}

/// <summary>The result of comparing two windows (PRD section 5 herald_window_diff).</summary>
public sealed record WindowDiffResult(
    IReadOnlyList<KindChange> NewKinds,
    IReadOnlyList<KindChange> GoneQuiet,
    IReadOnlyList<KindChange> Changed);

/// <summary>
/// Compares two windows of events by cluster signature (PRD section 5,
/// anchor A17). Semantics, all deterministic:
/// - NEW: a signature present in current, absent in baseline (baseline
///   count 0; reported as a count, never a ratio, so zero-baseline needs
///   no division).
/// - GONE-QUIET: a signature present in baseline, absent in current.
/// - CHANGED: a signature in both whose count differs; delta = current -
///   baseline.
/// Each section sorts by descending magnitude (CurrentCount for new and
/// gone-quiet, |Delta| for changed) with a signature tie-break, then top-N.
/// Grouping uses the same signature as herald_error_clusters.
/// </summary>
public static class WindowDiff
{
    public static WindowDiffResult Compare(
        IEnumerable<HeraldEvent> baseline, IEnumerable<HeraldEvent> current, int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var baseCounts = CountBySignature(baseline);
        var curCounts = CountBySignature(current);

        var newKinds = new List<KindChange>();
        var goneQuiet = new List<KindChange>();
        var changed = new List<KindChange>();

        foreach (var (sig, cur) in curCounts)
        {
            if (!baseCounts.TryGetValue(sig, out var bse))
                newKinds.Add(new KindChange { Signature = sig, BaselineCount = 0, CurrentCount = cur });
            else if (cur != bse)
                changed.Add(new KindChange { Signature = sig, BaselineCount = bse, CurrentCount = cur });
        }

        foreach (var (sig, bse) in baseCounts)
        {
            if (!curCounts.ContainsKey(sig))
                goneQuiet.Add(new KindChange { Signature = sig, BaselineCount = bse, CurrentCount = 0 });
        }

        return new WindowDiffResult(
            newKinds.OrderByDescending(k => k.CurrentCount).ThenBy(k => k.Signature, StringComparer.Ordinal).Take(topN).ToList(),
            goneQuiet.OrderByDescending(k => k.BaselineCount).ThenBy(k => k.Signature, StringComparer.Ordinal).Take(topN).ToList(),
            changed.OrderByDescending(k => Math.Abs(k.Delta)).ThenBy(k => k.Signature, StringComparer.Ordinal).Take(topN).ToList());
    }

    private static Dictionary<string, int> CountBySignature(IEnumerable<HeraldEvent> events)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var sig = ErrorClusterer.SignatureOf(e);
            counts[sig] = counts.GetValueOrDefault(sig) + 1;
        }
        return counts;
    }
}
