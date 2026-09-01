// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.Json;
using HeraldMcp.Core.Redaction;

namespace HeraldMcp.Tests.Redaction;

/// <summary>
/// A12 (PRD section 7.4): the default-on masker. The adversarial corpus
/// here is the "named, enumerated set" A12 requires — every family the
/// PRD promises, planted in prose, JSON values, and secret-named keys —
/// plus the false-positive corpus: benign look-alikes that must pass
/// unmasked, because the false-positive cost is part of the contract.
/// </summary>
public sealed class RedactionMaskerTests
{
    // ---- planted-secret corpus: every family must be masked ----

    [Theory]
    // bearer tokens
    [InlineData("Authorization: Bearer abcDEF0123456789abcDEF0123456789", "bearer")]
    [InlineData("sent Bearer x9y8z7w6v5u4t3s2r1q0p9o8n7m6 to the api", "bearer")]
    // JWTs (three base64url segments, eyJ prefix)
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U leaked", "jwt")]
    // connection-string credential pairs
    [InlineData("Server=db;User Id=app;Password=Sup3rS3cret!;Encrypt=true", "credential-pair")]
    [InlineData("AccountKey=YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0MjQyNDI= was in the log", "credential-pair")]
    [InlineData("set APIKEY=zx81c64spectrumZX48 before running", "credential-pair")]
    // private key blocks
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----", "private-key")]
    [InlineData("dumped -----BEGIN PRIVATE KEY----- MC4CAQAwBQYDK2VwBCIEIA -----END PRIVATE KEY----- to stderr", "private-key")]
    // cloud/vendor token shapes
    [InlineData("using AKIAIOSFODNN7EXAMPLE for s3", "aws-access-key")]
    [InlineData("pushed with ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 credentials", "vendor-token")]
    [InlineData("openai key sk-proj4abcdefghijklmnopqrstu was rejected", "vendor-token")]
    [InlineData("slack hook xoxb-123456789012-ABCDEFGHIJKLMNOPQRSTUVWX failed", "vendor-token")]
    // PII shapes
    [InlineData("user steve.muchow@example.com logged in", "email")]
    [InlineData("ssn on file 123-45-6789 rejected", "ssn")]
    [InlineData("card 4111 1111 1111 1111 declined", "payment-card")] // passes Luhn
    public void Planted_secret_is_masked(string text, string family)
    {
        var masked = RedactionMasker.MaskText(text);
        Assert.DoesNotContain(ExtractSecretCore(text, family), masked, StringComparison.Ordinal);
        Assert.Contains("[MASKED", masked, StringComparison.Ordinal);
    }

    // ---- false-positive corpus: benign look-alikes must survive ----

    [Theory]
    [InlineData("commit 3f2a9c81d4e5b6a7f8091a2b3c4d5e6f70819aab merged")] // git SHA
    [InlineData("request id 550e8400-e29b-41d4-a716-446655440000 timed out")] // UUID
    [InlineData("version 10.0.204 of the sdk")] // version string
    [InlineData("task-1234 assigned to the runner")] // 'sk-' inside a word
    [InlineData("card 1234 5678 9012 3456 declined")] // fails Luhn: stays
    [InlineData("Password prompt was shown to the user")] // key word, no '=value'
    [InlineData("at 2026-08-31T21:44:21.485Z the job started")] // timestamp
    [InlineData("the risk-benefit analysis completed")] // 'sk-b' inside a word
    [InlineData("counter went from 123-45-678 to done")] // 8 digits, not an SSN
    public void Benign_lookalike_is_untouched(string text)
    {
        Assert.Equal(text, RedactionMasker.MaskText(text));
    }

    // ---- semantics ----

    [Fact]
    public void Masking_is_idempotent()
    {
        const string text = "Bearer abcDEF0123456789abcDEF0123456789 and steve@example.com";
        var once = RedactionMasker.MaskText(text);
        Assert.Equal(once, RedactionMasker.MaskText(once));
    }

    [Fact]
    public void NonMatching_input_is_returned_unchanged_by_reference()
    {
        const string text = "an ordinary log line about a null reference";
        Assert.Same(text, RedactionMasker.MaskText(text));
    }

    [Fact]
    public void Multiple_families_in_one_line_are_all_masked()
    {
        const string text =
            "Bearer abcDEF0123456789abcDEF0123456789 for steve@example.com with Password=hunter2;";
        var masked = RedactionMasker.MaskText(text);
        Assert.DoesNotContain("abcDEF0123456789abcDEF0123456789", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("steve@example.com", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
    }

    // ---- JSON walking (tool results are JsonElement trees) ----

    [Fact]
    public void Json_string_values_are_masked_at_any_depth()
    {
        using var doc = JsonDocument.Parse(
            """{"m":"login with Bearer abcDEF0123456789abcDEF0123456789","p":{"who":"steve@example.com","n":[{"deep":"Password=oops;"}]}}""");
        var masked = RedactionMasker.MaskElement(doc.RootElement).ToJsonString();
        Assert.DoesNotContain("abcDEF0123456789abcDEF0123456789", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("steve@example.com", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("oops", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_named_json_key_masks_its_whole_value_regardless_of_shape()
    {
        using var doc = JsonDocument.Parse(
            """{"password":"tr0ub4dor","api_key":"shortval","client_secret":"x","note":"fine"}""");
        var node = RedactionMasker.MaskElement(doc.RootElement);
        var masked = node.ToJsonString();
        Assert.DoesNotContain("tr0ub4dor", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("shortval", masked, StringComparison.Ordinal);
        Assert.Contains("fine", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_nonstring_values_and_structure_survive_unchanged()
    {
        using var doc = JsonDocument.Parse(
            """{"count":42,"ok":true,"ratio":0.5,"none":null,"tags":["a","b"]}""");
        var masked = RedactionMasker.MaskElement(doc.RootElement).ToJsonString();
        using var round = JsonDocument.Parse(masked);
        Assert.Equal(42, round.RootElement.GetProperty("count").GetInt32());
        Assert.True(round.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, round.RootElement.GetProperty("tags").GetArrayLength());
    }

    // ---- fuzz sweep: masker must terminate fast and never throw ----

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(4242)]
    public void Fuzz_random_hostile_text_never_throws_and_stays_linear(int seed)
    {
        var rng = new Random(seed);
        var alphabet = "aB3=;-._@/+ \t\"\\{}[]eyJxox".AsSpan();
        for (var round = 0; round < 200; round++)
        {
            var len = rng.Next(0, 4096);
            var text = string.Create(len, (rng, alphabet.ToString()), static (span, s) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = s.Item2[s.rng.Next(s.Item2.Length)];
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = RedactionMasker.MaskText(text);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"seed {seed} round {round}: masking {len} chars took {sw.ElapsedMilliseconds} ms");
        }
    }

    private static string ExtractSecretCore(string text, string family) => family switch
    {
        "bearer" => "abcDEF0123456789abcDEF0123456789" is var t && text.Contains(t) ? t : text.Split("Bearer ")[1].Split(' ')[0],
        "jwt" => text.Split(' ').First(w => w.StartsWith("eyJ", StringComparison.Ordinal)),
        "credential-pair" => text.Contains("Sup3rS3cret!") ? "Sup3rS3cret!"
            : text.Contains("YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0MjQyNDI=") ? "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0MjQyNDI="
            : "zx81c64spectrumZX48",
        "private-key" => "PRIVATE KEY-----",
        "aws-access-key" => "AKIAIOSFODNN7EXAMPLE",
        "vendor-token" => text.Contains("ghp_") ? "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"
            : text.Contains("sk-") ? "sk-proj4abcdefghijklmnopqrstu"
            : "xoxb-123456789012-ABCDEFGHIJKLMNOPQRSTUVWX",
        "email" => "steve.muchow@example.com",
        "ssn" => "123-45-6789",
        "payment-card" => "4111 1111 1111 1111",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
