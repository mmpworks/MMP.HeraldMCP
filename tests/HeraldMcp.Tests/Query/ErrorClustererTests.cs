// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Query;

namespace HeraldMcp.Tests.Query;

/// <summary>
/// herald_error_clusters (PRD section 5, anchor A3): group events by
/// exception type + normalized message + top frame. Normalization is
/// linear-time with no unbounded regex (section 7.8), and its rules are
/// versioned so clustering is deterministic across releases.
/// </summary>
public sealed class ErrorClustererTests
{
    private static HeraldEvent Err(string message, string? exType = null, string? stack = null,
        DateTimeOffset? at = null) => new()
    {
        Time = at ?? new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
        LevelKey = "error",
        LevelRank = 4,
        Category = "App",
        Message = message,
        Exception = exType is null ? null : new HeraldException(exType, message, stack, null),
    };

    [Fact]
    public void Three_families_form_three_clusters_plus_a_singleton()
    {
        // A3: three exception families -> three clusters + one singleton.
        var events = new[]
        {
            Err("timeout after 30s", "System.TimeoutException", "at Db.Run()"),
            Err("timeout after 45s", "System.TimeoutException", "at Db.Run()"),
            Err("timeout after 12s", "System.TimeoutException", "at Db.Run()"),
            Err("null at index 7", "System.NullReferenceException", "at Svc.Get()"),
            Err("null at index 3", "System.NullReferenceException", "at Svc.Get()"),
            Err("bad arg count 5", "System.ArgumentException", "at Api.Call()"),
            Err("one off weirdness", "System.InvalidOperationException", "at X.Y()"),
        };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Equal(4, clusters.Count);
        Assert.Equal(3, clusters[0].Count); // timeouts, largest first
    }

    [Fact]
    public void Numbers_and_ids_are_normalized_so_variants_cluster_together()
    {
        var events = new[]
        {
            Err("request 12345 failed for user a1b2c3d4-e5f6-7890-abcd-ef0123456789"),
            Err("request 67890 failed for user 99999999-8888-7777-6666-555555555555"),
            Err("request 11111 failed for user 00000000-0000-0000-0000-000000000000"),
        };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Single(clusters);
        Assert.Equal(3, clusters[0].Count);
    }

    [Fact]
    public void Clusters_are_ordered_by_count_descending()
    {
        var events = new List<HeraldEvent>();
        events.AddRange(Enumerable.Repeat(0, 5).Select(_ => Err("common error", "TypeA")));
        events.AddRange(Enumerable.Repeat(0, 2).Select(_ => Err("rare error", "TypeB")));
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Equal(5, clusters[0].Count);
        Assert.Equal(2, clusters[1].Count);
    }

    [Fact]
    public void First_and_last_seen_span_the_cluster()
    {
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Err("x", "T", at: t0.AddMinutes(5)),
            Err("x", "T", at: t0),
            Err("x", "T", at: t0.AddMinutes(10)),
        };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Equal(t0, clusters[0].FirstSeen);
        Assert.Equal(t0.AddMinutes(10), clusters[0].LastSeen);
    }

    [Fact]
    public void One_exemplar_is_kept_per_cluster()
    {
        var events = new[] { Err("boom 1", "T"), Err("boom 2", "T") };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Single(clusters);
        Assert.NotNull(clusters[0].Exemplar);
        Assert.Contains("boom", clusters[0].Exemplar.RenderedMessage);
    }

    [Fact]
    public void TopN_caps_the_returned_clusters_but_counts_reflect_all()
    {
        var events = new List<HeraldEvent>();
        for (var i = 0; i < 20; i++)
            events.AddRange(Enumerable.Repeat(0, i + 1).Select(_ => Err($"kind {i}", $"Type{i}")));
        var clusters = ErrorClusterer.Cluster(events, topN: 3);
        Assert.Equal(3, clusters.Count);
        Assert.Equal(20, clusters[0].Count); // the largest family (i=19)
    }

    [Fact]
    public void Events_without_exceptions_cluster_by_normalized_message()
    {
        // The number is the variable part; the device is constant.
        var events = new[]
        {
            Err("disk usage 91% on /dev/sda"),
            Err("disk usage 87% on /dev/sda"),
        };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Single(clusters);
    }

    [Fact]
    public void Distinct_nonnumeric_identifiers_form_distinct_clusters()
    {
        // Boundary, stated honestly: normalization collapses numbers, GUIDs,
        // and long hex runs, but cannot tell a device name from a word, so
        // "sda" and "sdb" cluster separately. This is the false-split cost.
        var events = new[]
        {
            Err("disk full on /dev/sda"),
            Err("disk full on /dev/sdb"),
        };
        var clusters = ErrorClusterer.Cluster(events, topN: 10);
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void Empty_input_yields_no_clusters()
    {
        Assert.Empty(ErrorClusterer.Cluster(Array.Empty<HeraldEvent>(), topN: 10));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(55)]
    [InlineData(555)]
    public void Fuzz_normalization_is_linear_and_never_throws(int seed)
    {
        var rng = new Random(seed);
        var events = new List<HeraldEvent>();
        for (var i = 0; i < 500; i++)
        {
            var len = rng.Next(0, 2000);
            var msg = string.Create(len, rng, static (span, r) =>
            {
                for (var j = 0; j < span.Length; j++)
                    span[j] = (char)r.Next(32, 126);
            });
            events.Add(Err(msg, rng.Next(2) == 0 ? "T" : null));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var clusters = ErrorClusterer.Cluster(events, topN: 50);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"clustering 500 events took {sw.ElapsedMilliseconds} ms");
        Assert.True(clusters.Count <= 50);
    }
}
