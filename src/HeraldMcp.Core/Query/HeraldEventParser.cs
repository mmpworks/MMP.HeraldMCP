// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HeraldMcp.Core.Query;

/// <summary>
/// Parses Herald's shipped file formats into <see cref="HeraldEvent"/>
/// (PRD Q1). Two formats are in scope for v1: json_file (snake_case NDJSON)
/// and text_file. Neither parser throws on hostile input; both return null
/// on a line they cannot read, and the caller counts nulls as skipped.
/// </summary>
public static partial class HeraldEventParser
{
    // text_file: "[<time> <LEVEL>:<rank>] <Category>: <message> [k=v ...]"
    // Category runs to the first ": "; it may contain dots and spaces, so it
    // is captured lazily up to the first colon-space (Q1 upstream bug #3).
    [GeneratedRegex(@"^\[(?<time>[^\]\s]+)\s+(?<level>[A-Z]{3}):(?<rank>\d+)\]\s+(?<cat>.+?):\s(?<rest>.*)$")]
    private static partial Regex TextLine();

    // trailing "key=value" pairs, value up to whitespace.
    [GeneratedRegex(@"(?<k>[A-Za-z_][A-Za-z0-9_.]*)=(?<v>\S+)")]
    private static partial Regex KeyValue();

    private static readonly Dictionary<string, (string Key, int Rank)> DisplayToLevel = new(StringComparer.Ordinal)
    {
        ["TRC"] = ("verbose", 0),
        ["VRB"] = ("verbose", 0),
        ["DBG"] = ("debug", 1),
        ["INF"] = ("information", 2),
        ["WRN"] = ("warning", 3),
        ["ERR"] = ("error", 4),
        ["FTL"] = ("fatal", 5), // Q1 bug #3: sink emits FTL, the raw searcher expects CRT
        ["CRT"] = ("fatal", 5),
    };

    /// <summary>Parses a json_file NDJSON line. Leading whitespace is tolerated.</summary>
    public static HeraldEvent? ParseJsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("time", out var timeEl)
                || !TryParseTime(timeEl, out var time))
                return null;

            var levelKey = GetString(root, "level_key");
            var rank = GetInt(root, "level_rank");
            if (levelKey is null)
            {
                // Fall back to the display abbreviation if level_key is absent.
                var display = GetString(root, "level");
                if (display is not null && DisplayToLevel.TryGetValue(display, out var m))
                    (levelKey, rank) = (m.Key, rank ?? m.Rank);
            }
            levelKey ??= "information";

            return new HeraldEvent
            {
                Time = time,
                LevelKey = levelKey,
                LevelRank = rank ?? RankForKey(levelKey),
                Category = GetString(root, "category") ?? string.Empty,
                Message = GetString(root, "message") ?? string.Empty,
                Template = GetString(root, "message_template"),
                Properties = ReadProperties(root),
                Exception = ReadException(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a text_file line.</summary>
    public static HeraldEvent? ParseTextLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = TextLine().Match(line);
        if (!m.Success) return null;
        if (!DateTimeOffset.TryParse(m.Groups["time"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            return null;

        var display = m.Groups["level"].Value;
        var (levelKey, defaultRank) = DisplayToLevel.TryGetValue(display, out var mapped)
            ? mapped
            : (display.ToLowerInvariant(), 2);
        var rank = int.TryParse(m.Groups["rank"].Value, out var r) ? r : defaultRank;

        var rest = m.Groups["rest"].Value;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match kv in KeyValue().Matches(rest))
            props[kv.Groups["k"].Value] = kv.Groups["v"].Value;

        return new HeraldEvent
        {
            Time = time,
            LevelKey = levelKey,
            LevelRank = rank,
            Category = m.Groups["cat"].Value,
            Message = rest,
            Properties = props,
        };
    }

    private static bool TryParseTime(JsonElement el, out DateTimeOffset time)
    {
        time = default;
        if (el.ValueKind != JsonValueKind.String) return false;
        return DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, string> ReadProperties(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return EmptyProps;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in props.EnumerateObject())
        {
            // Each property is either a scalar or {value, capture_mode, format}.
            if (p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("value", out var v))
                result[p.Name] = ScalarText(v);
            else
                result[p.Name] = ScalarText(p.Value);
        }
        return result;
    }

    private static HeraldException? ReadException(JsonElement root)
    {
        if (!root.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var member in ctx.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.Object
                && member.Value.TryGetProperty("type", out _))
                return ReadExceptionNode(member.Value);
        }
        return null;
    }

    private static HeraldException? ReadExceptionNode(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("type", out var typeEl))
            return null;
        HeraldException? inner = null;
        if (el.TryGetProperty("inner", out var innerEl) && innerEl.ValueKind == JsonValueKind.Object)
            inner = ReadExceptionNode(innerEl);
        return new HeraldException(
            typeEl.GetString() ?? string.Empty,
            GetString(el, "message") ?? string.Empty,
            GetString(el, "stackTrace"),
            inner);
    }

    private static string ScalarText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => el.GetRawText(),
    };

    private static int RankForKey(string levelKey) => levelKey switch
    {
        "verbose" => 0,
        "debug" => 1,
        "information" => 2,
        "warning" => 3,
        "error" => 4,
        "fatal" => 5,
        _ => 2,
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyProps =
        new Dictionary<string, string>();
}
