// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeraldMcp.Core.Budgets;

/// <summary>Result-shaping caps (PRD section 7.5 / anchor A6).</summary>
public sealed record BudgetLimits(int MaxEvents, int MaxSerializedBytes, TimeSpan TokenTtl)
{
    /// <summary>v1 defaults from PRD section 7.5.</summary>
    public static readonly BudgetLimits Default = new(
        MaxEvents: 1000,
        MaxSerializedBytes: 1024 * 1024,
        TokenTtl: TimeSpan.FromMinutes(5));
}

/// <summary>One page of results plus its truncation signal.</summary>
public sealed record ResultPage(
    IReadOnlyList<JsonElement> Events,
    bool Truncated,
    string? ContinuationToken);

/// <summary>The decoded state carried by a continuation token.</summary>
public readonly record struct Continuation(int Skip);

/// <summary>A continuation token that is unparseable, tampered, for another query, or expired.</summary>
public sealed class InvalidContinuationTokenException(string message) : Exception(message);

/// <summary>
/// Applies the event and serialized-byte caps to a result stream and issues
/// query-bound, expiring continuation tokens (A6). A page is cut at
/// whichever cap is reached first; truncation is always signalled. Progress
/// is guaranteed: a single event larger than the byte cap is still returned
/// alone, so paging never stalls. Following tokens to the end reconstructs
/// the full match count. Tokens are HMAC-signed with a per-process key, so
/// a tampered or foreign token is refused rather than misread.
/// </summary>
public sealed class ResultBudget
{
    private const int MacLength = 16;
    private readonly BudgetLimits _limits;
    private readonly Func<DateTimeOffset> _clock;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public ResultBudget(BudgetLimits limits, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxSerializedBytes);
        _limits = limits;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public ResultPage Take(IEnumerable<JsonElement> events, int totalMatched, int skip, string query)
    {
        var kept = new List<JsonElement>(Math.Min(_limits.MaxEvents, 16));
        var bytes = 2; // "[]" framing
        var truncatedByStream = true; // set false only if the source runs dry
        var consumed = 0;

        using var enumerator = events.GetEnumerator();
        while (true)
        {
            if (!enumerator.MoveNext())
            {
                truncatedByStream = false;
                break;
            }

            if (kept.Count == _limits.MaxEvents) break;

            var element = enumerator.Current;
            var size = MeasureBytes(element) + 1; // + comma
            if (kept.Count > 0 && bytes + size > _limits.MaxSerializedBytes)
                break; // byte cap: keep what we have (at least one)

            kept.Add(element);
            bytes += size;
            consumed++;

            // A single over-cap event is allowed as the sole item, then stop.
            if (bytes > _limits.MaxSerializedBytes) break;
        }

        var nextSkip = skip + kept.Count;
        var more = nextSkip < totalMatched && (truncatedByStream || kept.Count < CountRemaining(totalMatched, skip));
        // "more" is true when the corpus has events past this page.
        more = nextSkip < totalMatched;

        return new ResultPage(
            kept,
            Truncated: more,
            ContinuationToken: more ? EncodeToken(nextSkip, query) : null);
    }

    private static int CountRemaining(int totalMatched, int skip) => Math.Max(0, totalMatched - skip);

    public string EncodeToken(int skip, string query)
    {
        var issued = _clock().ToUnixTimeMilliseconds();
        var payload = $"{skip}:{issued}:{QueryFingerprint(query)}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(_key, payloadBytes)[..MacLength];
        // Fixed-length trailing MAC: no in-band separator, because a binary
        // MAC can contain any byte including a separator character.
        var combined = new byte[payloadBytes.Length + MacLength];
        payloadBytes.CopyTo(combined, 0);
        mac.CopyTo(combined, payloadBytes.Length);
        return Base64Url(combined);
    }

    public Continuation DecodeToken(string token, string query)
    {
        byte[] raw;
        try { raw = FromBase64Url(token); }
        catch { throw new InvalidContinuationTokenException("The continuation token is malformed; restart the query."); }

        if (raw.Length <= MacLength)
            throw new InvalidContinuationTokenException("The continuation token is malformed; restart the query.");

        var payloadBytes = raw.AsSpan(0, raw.Length - MacLength);
        var mac = raw.AsSpan(raw.Length - MacLength);
        var expected = HMACSHA256.HashData(_key, payloadBytes).AsSpan(0, MacLength);
        if (mac.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(mac, expected))
            throw new InvalidContinuationTokenException("The continuation token is not valid here; restart the query.");

        var parts = Encoding.UTF8.GetString(payloadBytes).Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var skip)
            || !long.TryParse(parts[1], out var issued))
            throw new InvalidContinuationTokenException("The continuation token is malformed; restart the query.");

        if (parts[2] != QueryFingerprint(query))
            throw new InvalidContinuationTokenException("The continuation token belongs to a different query; restart the query.");

        var age = _clock() - DateTimeOffset.FromUnixTimeMilliseconds(issued);
        if (age > _limits.TokenTtl)
            throw new InvalidContinuationTokenException("The continuation token has expired; restart the query.");

        return new Continuation(skip);
    }

    private static string QueryFingerprint(string query)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(query ?? string.Empty));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private static int MeasureBytes(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            element.WriteTo(writer);
        return buffer.WrittenCount;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", 0 => padded, _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
