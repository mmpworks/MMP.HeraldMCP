// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text;

namespace HeraldMcp.Core.Query;

/// <summary>An event id that does not parse into a source and ordinal.</summary>
public sealed class InvalidEventIdException(string message) : Exception(message);

/// <summary>
/// The stable id for a single event (PRD Q3): a source id plus the event's
/// ordinal among the parseable events in the file, in scan order. The
/// ordinal is stable under append — a later append only adds higher
/// ordinals and never renumbers earlier events — which Q3 measured. The id
/// carries no path (the source id is already opaque).
/// </summary>
public static class EventId
{
    public static string Encode(string sourceId, int ordinal)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        var raw = $"{sourceId}:{ordinal}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static (string SourceId, int Ordinal) Decode(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new InvalidEventIdException("The event id is empty; take one from a herald_search result.");
        string raw;
        try
        {
            var padded = id.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", 0 => padded, _ => throw new FormatException() };
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            throw new InvalidEventIdException("The event id is malformed; take one from a herald_search result.");
        }

        var sep = raw.LastIndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1 || !int.TryParse(raw.AsSpan(sep + 1), out var ordinal) || ordinal < 0)
            throw new InvalidEventIdException("The event id is malformed; take one from a herald_search result.");

        return (raw[..sep], ordinal);
    }
}
