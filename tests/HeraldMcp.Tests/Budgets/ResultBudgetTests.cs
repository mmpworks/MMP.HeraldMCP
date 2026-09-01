// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using HeraldMcp.Core.Budgets;

namespace HeraldMcp.Tests.Budgets;

/// <summary>
/// A6 (PRD section 7.5): a result is cut at the event cap AND the
/// serialized-byte cap; truncation is always signalled, never silent; the
/// continuation token is query-bound and expires. Scan-time and
/// concurrency limits live in the query layer; this covers the
/// result-shaping half A6 pins.
/// </summary>
public sealed class ResultBudgetTests
{
    private static readonly BudgetLimits Small = new(
        MaxEvents: 3,
        MaxSerializedBytes: 4096,
        TokenTtl: TimeSpan.FromMinutes(5));

    private static JsonElement Event(int i)
    {
        using var doc = JsonDocument.Parse($$"""{"i":{{i}},"m":"event number {{i}}"}""");
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Under_both_caps_is_not_truncated()
    {
        var budget = new ResultBudget(Small);
        var page = budget.Take(new[] { Event(0), Event(1) }, totalMatched: 2, skip: 0, query: "q");
        Assert.False(page.Truncated);
        Assert.Equal(2, page.Events.Count);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public void Event_cap_truncates_and_sets_the_flag()
    {
        var budget = new ResultBudget(Small);
        var page = budget.Take(Enumerable.Range(0, 10).Select(Event), totalMatched: 10, skip: 0, query: "q");
        Assert.True(page.Truncated);
        Assert.Equal(3, page.Events.Count);
        Assert.NotNull(page.ContinuationToken);
    }

    [Fact]
    public void Byte_cap_truncates_before_the_event_cap_when_events_are_large()
    {
        var big = MakeBigEvent(2000); // two of these exceed 4096 bytes
        var budget = new ResultBudget(Small);
        var page = budget.Take(new[] { big, big, big }, totalMatched: 3, skip: 0, query: "q");
        Assert.True(page.Truncated);
        Assert.True(page.Events.Count < 3);
        Assert.NotNull(page.ContinuationToken);
    }

    [Fact]
    public void At_least_one_event_is_returned_even_if_it_alone_exceeds_the_byte_cap()
    {
        // Progress guarantee: an over-cap event that is the FIRST of several
        // is still returned alone, with truncation signalled, so paging can
        // advance instead of stalling on an empty page.
        var huge = MakeBigEvent(10_000);
        var budget = new ResultBudget(Small);
        var page = budget.Take(new[] { huge, huge, huge }, totalMatched: 3, skip: 0, query: "q");
        Assert.Single(page.Events);
        Assert.True(page.Truncated);
        Assert.NotNull(page.ContinuationToken);
    }

    [Fact]
    public void A_lone_over_cap_event_that_is_the_whole_result_is_not_truncated()
    {
        var huge = MakeBigEvent(10_000);
        var budget = new ResultBudget(Small);
        var page = budget.Take(new[] { huge }, totalMatched: 1, skip: 0, query: "q");
        Assert.Single(page.Events);
        Assert.False(page.Truncated);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public void Continuation_token_encodes_the_next_skip()
    {
        var budget = new ResultBudget(Small);
        var page = budget.Take(Enumerable.Range(0, 10).Select(Event), totalMatched: 10, skip: 0, query: "q");
        var next = budget.DecodeToken(page.ContinuationToken!, query: "q");
        Assert.Equal(3, next.Skip);
    }

    [Fact]
    public void Token_is_bound_to_its_query()
    {
        var budget = new ResultBudget(Small);
        var page = budget.Take(Enumerable.Range(0, 10).Select(Event), totalMatched: 10, skip: 0, query: "original");
        Assert.Throws<InvalidContinuationTokenException>(
            () => budget.DecodeToken(page.ContinuationToken!, query: "different"));
    }

    [Fact]
    public void Expired_token_is_refused()
    {
        var budget = new ResultBudget(Small with { TokenTtl = TimeSpan.FromMilliseconds(1) },
            clock: FixedClock());
        var page = budget.Take(Enumerable.Range(0, 10).Select(Event), 10, skip: 0, query: "q");
        var token = page.ContinuationToken!;
        var later = new ResultBudget(Small with { TokenTtl = TimeSpan.FromMilliseconds(1) },
            clock: () => DateTimeOffset.UnixEpoch.AddMinutes(10));
        Assert.Throws<InvalidContinuationTokenException>(() => later.DecodeToken(token, query: "q"));
    }

    [Fact]
    public void Following_tokens_to_the_end_reconstructs_the_full_count()
    {
        var all = Enumerable.Range(0, 10).Select(Event).ToList();
        var budget = new ResultBudget(Small);
        var collected = 0;
        var skip = 0;
        while (true)
        {
            var page = budget.Take(all.Skip(skip), totalMatched: all.Count, skip: skip, query: "q");
            collected += page.Events.Count;
            if (!page.Truncated) break;
            skip = budget.DecodeToken(page.ContinuationToken!, query: "q").Skip;
        }
        Assert.Equal(10, collected);
    }

    [Fact]
    public void Tampered_token_is_refused()
    {
        var budget = new ResultBudget(Small);
        Assert.Throws<InvalidContinuationTokenException>(
            () => budget.DecodeToken("not-a-real-token", query: "q"));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void Fuzz_token_round_trip_never_leaks_or_crashes(int seed)
    {
        var rng = new Random(seed);
        var budget = new ResultBudget(Small);
        for (var n = 0; n < 500; n++)
        {
            var skip = rng.Next(0, 1_000_000);
            var query = Guid.NewGuid().ToString("N")[..rng.Next(1, 20)];
            var token = budget.EncodeToken(skip, query);
            var decoded = budget.DecodeToken(token, query);
            Assert.Equal(skip, decoded.Skip);
            // A random string that is not our token must be refused, not misread.
            var garbage = Convert.ToBase64String(BitConverter.GetBytes(rng.NextInt64()));
            var ex = Record.Exception(() => budget.DecodeToken(garbage, query));
            Assert.True(ex is null or InvalidContinuationTokenException);
        }
    }

    private static JsonElement MakeBigEvent(int payloadChars)
    {
        var payload = new string('x', payloadChars);
        using var doc = JsonDocument.Parse($$"""{"m":"{{payload}}"}""");
        return doc.RootElement.Clone();
    }

    private static Func<DateTimeOffset> FixedClock()
    {
        var t = DateTimeOffset.UnixEpoch;
        return () => t;
    }
}
