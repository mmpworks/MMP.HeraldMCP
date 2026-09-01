// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Sources;

/// <summary>An id that matches no current source (PRD section 5). One plain sentence.</summary>
public sealed class UnknownSourceException(string message) : Exception(message);

/// <summary>
/// An id that once resolved but whose file changed identity — a prune or
/// in-place replacement (PRD section 10). Refused, never remapped.
/// </summary>
public sealed class StaleSourceException(string message) : Exception(message);

/// <summary>
/// The served corpus exceeds the declared supported ceiling (PRD section
/// 4). Refused with a plain sentence rather than degrading silently.
/// </summary>
public sealed class CorpusCeilingExceededException(string message) : Exception(message);
