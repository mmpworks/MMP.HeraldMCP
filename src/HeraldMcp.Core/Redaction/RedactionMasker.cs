// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HeraldMcp.Core.Redaction;

/// <summary>
/// The default-on read-boundary masker (PRD section 7.4, anchor A12). New
/// code in this repo: Herald's own redaction runs at emit time and matches
/// property names, so it cannot serve here — this masker pattern-scans
/// CONTENT after the searcher returns it and before anything is
/// serialized into a tool result. Masking runs before truncation.
///
/// Residual, stated: a heuristic masker has a false-negative boundary (a
/// novel secret shape passes) and a false-positive cost (a benign string
/// that fits a family — e.g. a Luhn-valid digit run — is masked). It
/// reduces disclosure; it does not guarantee absence.
/// </summary>
public static class RedactionMasker
{
    private const string MaskFormat = "[MASKED:{0}]";

    /// <summary>
    /// JSON property names whose entire string value is masked regardless
    /// of shape. Comparison is case-insensitive.
    /// </summary>
    private static readonly HashSet<string> SecretNamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pwd", "passwd", "secret", "client_secret", "clientsecret",
        "apikey", "api_key", "apitoken", "api_token", "access_token", "accesstoken",
        "accesskey", "access_key", "accountkey", "account_key",
        "sharedaccesskey", "shared_access_key", "connectionstring", "connection_string",
        "private_key", "privatekey", "authorization", "credential", "credentials",
        "sas", "token", "bearer",
    };

    /// <summary>
    /// Masks every recognized secret family in <paramref name="text"/>.
    /// Returns the SAME string instance when nothing matched, so callers
    /// can cheaply detect "nothing to do".
    /// </summary>
    public static string MaskText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Order: block/base shapes first so a later, looser family never
        // re-writes an already-masked region.
        var result = text;
        result = Mask(result, RedactionPatterns.PrivateKeyBlock(), "private-key");
        result = Mask(result, RedactionPatterns.Jwt(), "jwt");
        result = Mask(result, RedactionPatterns.BearerToken(), "bearer");
        result = MaskCredentialPairs(result);
        result = Mask(result, RedactionPatterns.AwsAccessKeyId(), "aws-access-key");
        result = Mask(result, RedactionPatterns.VendorToken(), "vendor-token");
        result = Mask(result, RedactionPatterns.Email(), "email");
        result = Mask(result, RedactionPatterns.Ssn(), "ssn");
        result = MaskLuhnValidCards(result);
        return result;
    }

    /// <summary>
    /// Walks a <see cref="JsonElement"/> and returns a copy with every
    /// string value masked. A property whose NAME is in the secret-named
    /// set has its whole string value masked regardless of shape.
    /// Non-string values and structure are preserved.
    /// </summary>
    public static JsonNode MaskElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => MaskObject(element),
        JsonValueKind.Array => MaskArray(element),
        JsonValueKind.String => JsonValue.Create(MaskText(element.GetString() ?? string.Empty)),
        _ => JsonNode.Parse(element.GetRawText())!,
    };

    private static JsonObject MaskObject(JsonElement element)
    {
        var result = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && SecretNamedKeys.Contains(property.Name))
            {
                result[property.Name] = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, MaskFormat, "named-key");
                continue;
            }
            result[property.Name] = MaskElement(property.Value);
        }
        return result;
    }

    private static JsonArray MaskArray(JsonElement element)
    {
        var result = new JsonArray();
        foreach (var item in element.EnumerateArray())
            result.Add(MaskElement(item));
        return result;
    }

    private static string Mask(string input, Regex pattern, string family)
    {
        // Regex.Replace returns the original instance when nothing
        // matches, which preserves the same-reference contract.
        return pattern.Replace(input, string.Format(
            System.Globalization.CultureInfo.InvariantCulture, MaskFormat, family));
    }

    private static string MaskCredentialPairs(string input)
    {
        return RedactionPatterns.CredentialPair().Replace(input, static match =>
            $"{match.Groups["key"].Value}=[MASKED:credential-pair]");
    }

    private static string MaskLuhnValidCards(string input)
    {
        return RedactionPatterns.PaymentCardCandidate().Replace(input, static match =>
        {
            Span<char> digits = stackalloc char[19];
            var count = 0;
            foreach (var c in match.ValueSpan)
            {
                if (!char.IsAsciiDigit(c)) continue;
                if (count >= digits.Length) return match.Value; // too long: not a card
                digits[count++] = c;
            }
            return count is >= 13 and <= 19 && PassesLuhn(digits[..count])
                ? "[MASKED:payment-card]"
                : match.Value;
        });
    }

    private static bool PassesLuhn(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }
}
