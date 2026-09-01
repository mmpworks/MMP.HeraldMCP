// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Paths;

/// <summary>
/// Thrown when a requested path resolves outside every configured root
/// (PRD section 7.3). The message is one plain sentence naming the remedy,
/// per the tool-result error contract; it never echoes the out-of-root
/// path it refused.
/// </summary>
public sealed class PathConfinementException(string message) : Exception(message);
