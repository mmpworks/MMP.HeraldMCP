// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text;

namespace HeraldMcp.Core.Query;

/// <summary>One group of like events (PRD section 5 herald_error_clusters).</summary>
public sealed record ErrorCluster
{
    public required string Signature { get; init; }
    public required int Count { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>One representative event, so the caller sees a concrete instance.</summary>
    public required HeraldEvent Exemplar { get; init; }
}

/// <summary>
/// Groups events by a stable signature: exception type + normalized
/// message + top stack frame (PRD section 5, anchor A3). Normalization is
/// linear-time and rule-versioned so clustering is deterministic across
/// releases (section 7.8: no unbounded regex on attacker-writable input).
/// </summary>
public static class ErrorClusterer
{
    /// <summary>
    /// Bump when the normalization rules change; the version is part of the
    /// signature so a rule change cannot silently merge or split clusters.
    /// </summary>
    public const int NormalizationVersion = 1;

    public static IReadOnlyList<ErrorCluster> Cluster(IEnumerable<HeraldEvent> events, int topN)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var sig = Signature(e);
            if (!groups.TryGetValue(sig, out var acc))
            {
                acc = new Accumulator(sig, e);
                groups[sig] = acc;
            }
            acc.Add(e);
        }

        return groups.Values
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Signature, StringComparer.Ordinal) // stable tie-break
            .Take(topN)
            .Select(a => a.ToCluster())
            .ToList();
    }

    /// <summary>The cluster signature for one event, shared with window-diff so both group identically.</summary>
    public static string SignatureOf(HeraldEvent e) => Signature(e);

    private static string Signature(HeraldEvent e)
    {
        var sb = new StringBuilder(64);
        sb.Append('v').Append(NormalizationVersion).Append('|');
        sb.Append(e.Exception?.Type ?? "-").Append('|');
        sb.Append(TopFrame(e.Exception?.StackTrace)).Append('|');
        NormalizeMessageInto(sb, e.Exception?.Message ?? e.RenderedMessage);
        return sb.ToString();
    }

    private static string TopFrame(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return "-";
        var newline = stackTrace.IndexOfAny(['\r', '\n']);
        var first = newline < 0 ? stackTrace : stackTrace[..newline];
        return first.Trim();
    }

    /// <summary>
    /// Replaces the variable parts of a message with fixed placeholders in
    /// a single left-to-right pass: GUIDs, hex runs, and digit runs. No
    /// backtracking regex; the scan is O(message length).
    /// </summary>
    private static void NormalizeMessageInto(StringBuilder sb, string message)
    {
        var i = 0;
        var n = message.Length;
        while (i < n)
        {
            var c = message[i];
            if (char.IsAsciiHexDigit(c) || c == '-')
            {
                var start = i;
                var hyphens = 0;
                var hexOrHyphen = 0;
                while (i < n && (char.IsAsciiHexDigit(message[i]) || message[i] == '-'))
                {
                    if (message[i] == '-') hyphens++;
                    hexOrHyphen++;
                    i++;
                }
                var run = message.AsSpan(start, i - start);
                if (hyphens == 4 && run.Length == 36) sb.Append("<guid>");
                else if (IsAllDigits(run)) sb.Append("<n>");
                else if (hyphens == 0 && run.Length >= 8) sb.Append("<hex>");
                else sb.Append(run); // ordinary token, keep it
            }
            else if (char.IsAsciiDigit(c))
            {
                while (i < n && char.IsAsciiDigit(message[i])) i++;
                sb.Append("<n>");
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiDigit(c)) return false;
        return s.Length > 0;
    }

    private sealed class Accumulator(string signature, HeraldEvent exemplar)
    {
        public string Signature { get; } = signature;
        public int Count { get; private set; }
        private DateTimeOffset _first = DateTimeOffset.MaxValue;
        private DateTimeOffset _last = DateTimeOffset.MinValue;

        public void Add(HeraldEvent e)
        {
            Count++;
            if (e.Time < _first) _first = e.Time;
            if (e.Time > _last) _last = e.Time;
        }

        public ErrorCluster ToCluster() => new()
        {
            Signature = Signature,
            Count = Count,
            FirstSeen = _first,
            LastSeen = _last,
            Exemplar = exemplar,
        };
    }
}
