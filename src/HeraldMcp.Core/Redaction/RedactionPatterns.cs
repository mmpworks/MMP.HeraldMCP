// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Text.RegularExpressions;

namespace HeraldMcp.Core.Redaction;

/// <summary>
/// The enumerated pattern families A12 tests against (PRD section 7.4).
/// Every pattern is built from character classes and bounded quantifiers
/// with no nested quantifiers, so none can backtrack catastrophically
/// (section 7.8 forbids unbounded regex on attacker-writable input).
/// Source-generated for AOT.
/// </summary>
internal static partial class RedactionPatterns
{
    // -----BEGIN ... PRIVATE KEY----- through its END line (or to end of
    // input when the END marker is missing).
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]{0,32}PRIVATE KEY-----(?s:.){0,65536}?(?:-----END [A-Z0-9 ]{0,32}PRIVATE KEY-----|\z)")]
    internal static partial Regex PrivateKeyBlock();

    // Three base64url segments with the {"typ"/"alg" JSON header prefix.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}")]
    internal static partial Regex Jwt();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-_.=+/]{16,}", RegexOptions.None)]
    internal static partial Regex BearerToken();

    // key=value pairs whose key names a credential. The value runs to the
    // first separator, quote, or whitespace.
    [GeneratedRegex(
        @"\b(?<key>password|pwd|passwd|secret|client_secret|clientsecret|apikey|api_key|apitoken|api_token|access_token|accesstoken|accesskey|access_key|accountkey|account_key|sharedaccesskey|shared_access_key|sharedaccesssignature|private_key|privatekey|sas|token|auth)\s*=\s*(?<value>[^;&\s""']{1,512})",
        RegexOptions.IgnoreCase)]
    internal static partial Regex CredentialPair();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    internal static partial Regex AwsAccessKeyId();

    // GitHub (ghp_/gho_/ghu_/ghs_/ghr_), OpenAI-style sk-, Slack xox?-.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,255}|sk-[A-Za-z0-9\-_]{20,255}|xox[baprs]-[A-Za-z0-9-]{10,255})")]
    internal static partial Regex VendorToken();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,255}\.[A-Za-z]{2,24}\b")]
    internal static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)")]
    internal static partial Regex Ssn();

    // Candidate payment-card digit runs; the masker applies a Luhn check
    // before masking, which is the false-positive control.
    [GeneratedRegex(@"(?<![\d-])\d(?:[ -]?\d){12,18}(?![\d-])")]
    internal static partial Regex PaymentCardCandidate();
}
